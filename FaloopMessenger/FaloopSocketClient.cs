using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;

namespace FaloopMessenger;

public enum ConnectionState { Disconnected, Connecting, Connected }

// Minimal Socket.IO v4 client over .NET's native WebSocket.
//
// Engine.IO packet type is the first character:
//   '0'=OPEN  '2'=PING  '3'=PONG  '4'=MESSAGE  '5'=UPGRADE
// When type is '4' (MESSAGE), the second character is the Socket.IO packet type:
//   '0'=CONNECT  '1'=DISCONNECT  '2'=EVENT  '4'=ERROR
// Events look like: 42["eventName",{...data...}]
//
// Faloop-specific:
//   - Socket.IO path: /comms/socket.io
//   - Requires login: POST /api/auth/user/refresh → POST /api/auth/user/login
//   - Auth passed in Socket.IO CONNECT packet: 40{"sessionid":"<id>"}
//   - After CONNECT ack: emit 42["ack"]
//   - Events arrive as: 42["message",{"type":"mob","subType":"report","data":{...}}]
public class FaloopSocketClient : IDisposable
{
    public ConnectionState State     { get; private set; } = ConnectionState.Disconnected;
    public string?         LastError { get; private set; }

    public event System.Action?            OnUpdate;
    public event System.Action<SpawnInfo>? OnNewSpawn;

    // Faloop endpoints in one place. The Socket.IO URL stays configurable
    // (Configuration.SocketUrl) for power users; the REST + origin are stable.
    private const string FaloopOrigin     = "https://faloop.app";
    private const string FaloopBase       = "https://faloop.app/";
    private const string FaloopLoginRef   = "https://faloop.app/login";
    private const string ApiRefresh       = "https://faloop.app/api/auth/user/refresh";
    private const string ApiLogin         = "https://faloop.app/api/auth/user/login";

    // Browser-mimicking UA sent on both the websocket upgrade and the REST
    // calls (Faloop's endpoints are SPA-facing and reject obvious bots).
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 Edg/122.0.0.0";

    // A coordinate correction (real→real move) must exceed this many map-
    // units to re-echo. Debounces Faloop's incremental location refinements
    // — reporters nudging a marker a fraction of a tile shouldn't spam chat;
    // a genuine "wrong spot, it's actually over here" correction will.
    private const float CoordCorrectionThreshold = 2.0f;

    private readonly Configuration   _config;
    private readonly List<SpawnInfo> _spawns = new();
    private readonly object          _lock   = new();
    private readonly object          _lifecycle = new();

    // M-4 (v0.4.14 review): ONE HttpClient for the client's lifetime.
    // Previously every auth attempt and every 30-min time-sync tick built a
    // fresh HttpClient — each instance owns a connection pool, so per-call
    // construction is the classic socket-exhaustion anti-pattern. Shared
    // defaults live here; per-request Referer / Authorization are attached
    // to individual HttpRequestMessages. Disposed alongside the loop task.
    private readonly HttpClient _http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add("Accept",          "application/json, text/plain, */*");
        http.DefaultRequestHeaders.Add("Accept-Language", "en");
        http.DefaultRequestHeaders.Add("Origin",          FaloopOrigin);
        http.DefaultRequestHeaders.Add("User-Agent",      UserAgent);
        return http;
    }

    // JSON POST with the per-request headers Faloop's SPA-facing endpoints
    // expect. Caller disposes (or lets the response own it via SendAsync).
    private static HttpRequestMessage NewApiPost(string url, string referer, string jsonBody, string? authorization = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("Referer", referer);
        if (authorization != null)
            req.Headers.TryAddWithoutValidation("Authorization", authorization);
        return req;
    }
    private          CancellationTokenSource _cts = new();
    private          Task?  _loopTask;
    private          string? _sessionId;

    // Immutable snapshot rebuilt under _lock only when _spawns actually
    // changes. The UI reads this every frame (60–144 Hz); a volatile array
    // reference read is atomic and allocation-free, so the old per-frame
    // _spawns.ToArray() GC churn is gone.
    private volatile SpawnInfo[] _snapshot = System.Array.Empty<SpawnInfo>();

    // Set on every received frame; the liveness watchdog aborts the socket if
    // this goes stale (a half-open TCP connection never throws on its own).
    // long + Volatile.* (not `volatile int`) so the delta math can't break on
    // Environment.TickCount's 24.9-day Int32 wrap.
    private long _lastRxTick;

    public FaloopSocketClient(Configuration config) => _config = config;

    // ── Public API ────────────────────────────────────────────────────

    // Allocation-free: returns the cached immutable snapshot (rebuilt only on
    // mutation). IReadOnlyList so callers iterate without copying.
    public IReadOnlyList<SpawnInfo> GetSnapshot() => _snapshot;

    // Must be called inside `lock (_lock)` after any change to _spawns.
    private void RebuildSnapshotLocked() => _snapshot = _spawns.ToArray();

    // Manually remove a spawn from the tracker (e.g. user dismissing a stale entry).
    public void RemoveSpawn(SpawnInfo spawn)
    {
        bool removed;
        lock (_lock)
        {
            removed = _spawns.Remove(spawn);
            if (removed) RebuildSnapshotLocked();
        }
        if (removed) OnUpdate?.Invoke();
    }

    // Drop every tracked spawn. Used by /faloopoff so a paused session
    // doesn't leave stale, age-incrementing cards on screen. Cheap; the
    // OnUpdate fire lets the renderer cull its per-spawn animation state.
    public void ClearAll()
    {
        bool hadAny;
        lock (_lock)
        {
            hadAny = _spawns.Count > 0;
            if (hadAny)
            {
                _spawns.Clear();
                RebuildSnapshotLocked();
            }
        }
        if (hadAny) OnUpdate?.Invoke();
    }

    public void Connect()
    {
        lock (_lifecycle)
        {
            if (State is ConnectionState.Connecting or ConnectionState.Connected) return;
            _cts      = new CancellationTokenSource();
            _loopTask = ConnectLoop(_cts.Token);
        }
    }

    public void Disconnect()
    {
        _cts.Cancel();
        SetState(ConnectionState.Disconnected);
    }

    // Fully tears the old loop down (awaiting its exit and disposing its CTS)
    // before starting a fresh one — so two ConnectLoops can never run
    // concurrently and the CancellationTokenSource isn't leaked per reconnect.
    public void Reconnect() => _ = RestartAsync();

    private async Task RestartAsync()
    {
        CancellationTokenSource old;
        Task?                   oldTask;
        lock (_lifecycle) { old = _cts; oldTask = _loopTask; }

        old.Cancel();
        if (oldTask != null)
        {
            try { await oldTask.ConfigureAwait(false); }
            catch { /* loop exits via cancellation — expected */ }
        }
        old.Dispose();

        lock (_lifecycle)
        {
            _cts      = new CancellationTokenSource();
            _loopTask = ConnectLoop(_cts.Token);
        }
    }

    // ── Connection loop ───────────────────────────────────────────────

    private async Task ConnectLoop(CancellationToken ct)
    {
        // Exponential backoff: 5s → 10s → 30s → 60s → 120s cap. Resets to 5s
        // after every successful connection.
        var consecutiveFailures = 0;

        // Periodic time-sync background task (started after first connect).
        // Linked to ct, and guaranteed cancelled + disposed in the finally so
        // it can never outlive this loop (RestartAsync also awaits this whole
        // task before starting a new one, so loops never overlap).
        Task? timeSyncTask = null;
        var timeSyncCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
        while (!ct.IsCancellationRequested)
        {
            SetState(ConnectionState.Connecting);
            var connectedAt = DateTime.MinValue;
            try
            {
                // T-2: never send the session id over a non-TLS socket. The
                // URL is a free-text Advanced setting; a ws:// value would leak
                // it. This is a hard stop (no retry) — the user must fix the
                // setting and hit Reconnect; spinning wouldn't help.
                var wsUrl = _config.SocketUrl;
                if (!Uri.TryCreate(wsUrl, UriKind.Absolute, out var wsUri) ||
                    wsUri.Scheme != "wss")
                {
                    Plugin.Log.Error(
                        $"[Faloop] Refusing non-wss socket URL '{wsUrl}'. " +
                        "Fix it in Settings → Advanced, then Reconnect.");
                    LastError = "Socket URL must be wss://";
                    SetState(ConnectionState.Disconnected);
                    break;
                }

                // m-3 (v0.4.7 audit): SocketUrl is a free-text Advanced setting.
                // The session ID is sent through this socket as the auth token,
                // so a redirected host could capture a live session. Log a loud
                // warning when the host isn't faloop.app; don't block (power
                // users may proxy through a mirror), just surface the surprise.
                if (!wsUri.Host.Equals("faloop.app", StringComparison.OrdinalIgnoreCase) &&
                    !wsUri.Host.Equals("www.faloop.app", StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log.Warning(
                        $"[Faloop] Socket host is '{wsUri.Host}' (not faloop.app). " +
                        "Your session ID will be sent to this host. " +
                        "If you didn't intend this, restore the default URL in Settings → Advanced.");
                }

                _sessionId = await FetchAnonSession(ct);

                Plugin.Log.Information($"[Faloop] Connecting to {wsUrl}");

                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Origin",     FaloopOrigin);
                ws.Options.SetRequestHeader("User-Agent", UserAgent);

                await ws.ConnectAsync(wsUri, ct).ConfigureAwait(false);

                connectedAt = DateTime.UtcNow;
                timeSyncTask ??= TimeSyncLoop(timeSyncCts.Token);

                await RunSession(ws, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Plugin.Log.Warning($"[Faloop] Connection error: {ex.Message}");
                SetState(ConnectionState.Disconnected);
            }

            if (ct.IsCancellationRequested) break;

            // C-2: only a *sustained* connection resets the backoff. A socket
            // that opens then drops immediately (auth kick, server close,
            // rate-limit) must still back off — otherwise we'd re-hit the
            // auth endpoints every 5s and get ourselves throttled/banned.
            var sustained = connectedAt != DateTime.MinValue &&
                            DateTime.UtcNow - connectedAt > TimeSpan.FromSeconds(30);
            if (sustained) consecutiveFailures = 0;
            else           consecutiveFailures++;

            var delaySec = Math.Min(120, 5 * (int)Math.Pow(2, Math.Min(consecutiveFailures, 5)));
            if (consecutiveFailures > 1)
                Plugin.Log.Information($"[Faloop] Reconnect attempt #{consecutiveFailures + 1} in {delaySec}s");
            await Task.Delay(TimeSpan.FromSeconds(delaySec), ct).ConfigureAwait(false);
        }
        }
        finally
        {
            timeSyncCts.Cancel();
            timeSyncCts.Dispose();
            SetState(ConnectionState.Disconnected);
        }
    }

    // Periodically refresh the local↔server clock offset by reading the Date
    // header on a lightweight HEAD request to faloop.app. Runs in the
    // background for as long as we're connected.
    private async Task TimeSyncLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(30), ct);
                // M-4: shared client — a HEAD every 30 min through the same
                // connection pool instead of a fresh HttpClient per tick.
                using var req  = new HttpRequestMessage(HttpMethod.Head, FaloopBase);
                using var resp = await _http.SendAsync(req, ct);
                if (resp.Headers.Date.HasValue)
                    TimeSync.RecordServerTime(resp.Headers.Date.Value);
            }
            catch (OperationCanceledException) { break; }
            catch { /* transient — try again in 30 min */ }
        }
    }

    // ── Faloop session ────────────────────────────────────────────────

    // If credentials are configured, do the full refresh→login flow and return
    // the authenticated session ID. Otherwise just return the anonymous session
    // ID from refresh. Anonymous sessions appear to connect but never receive
    // events, so logging in is recommended.
    private async Task<string?> FetchAnonSession(CancellationToken ct)
    {
        // Snapshot credentials once so a concurrent Configuration.Save()
        // (which seals the password) can't make the two reads below disagree.
        var cfgUser = _config.Username;
        var cfgPass = _config.Password;

        try
        {
            // Step 1: refresh — pass the previously cached session ID so the
            // server can resume it if still valid (saves a roundtrip and
            // reduces auth churn). The server returns a fresh ID if the cached
            // one expired or was never set.
            var cachedSession = string.IsNullOrWhiteSpace(_config.StoredSessionId)
                ? "null"
                : $"\"{_config.StoredSessionId}\"";
            var refreshBody = $"{{\"sessionId\":{cachedSession}}}";
            using var refreshReq  = NewApiPost(ApiRefresh, FaloopBase, refreshBody);
            using var refreshResp = await _http.SendAsync(refreshReq, ct);
            if (!refreshResp.IsSuccessStatusCode)
            {
                Plugin.Log.Warning($"[Faloop] Refresh HTTP {(int)refreshResp.StatusCode}");
                return null;
            }

            // Capture server time from the Date header so our age timers stay
            // in sync with Faloop's website. Free side-effect of this call.
            if (refreshResp.Headers.Date.HasValue)
                TimeSync.RecordServerTime(refreshResp.Headers.Date.Value);

            var refreshJson = await refreshResp.Content.ReadAsStringAsync(ct);
            using var rDoc = JsonDocument.Parse(refreshJson);
            if (!rDoc.RootElement.TryGetProperty("success", out var rOk) || !rOk.GetBoolean()) return null;

            var rData       = rDoc.RootElement.GetProperty("data");
            var anonSession = rData.GetProperty("sessionId").GetString();
            var token       = rData.GetProperty("token").GetString();

            // No credentials → return anonymous session (may not receive events)
            if (string.IsNullOrWhiteSpace(cfgUser) || string.IsNullOrWhiteSpace(cfgPass))
            {
                Plugin.Log.Information("[Faloop] Using anonymous session (no credentials set).");
                CacheSession(anonSession);
                return anonSession;
            }

            // Step 2: login — uses anon sessionId + JWT to authenticate.
            // The token starts with "JWT " — passed through unvalidated
            // (the default AuthenticationHeaderValue parser rejects schemes
            // containing dots), attached per-request via NewApiPost.
            var loginPayload = JsonSerializer.Serialize(new
            {
                username   = cfgUser,
                password   = cfgPass,
                rememberMe = false,
                sessionId  = anonSession,
            });
            using var loginReq  = NewApiPost(ApiLogin, FaloopLoginRef, loginPayload, token);
            using var loginResp = await _http.SendAsync(loginReq, ct);
            if (!loginResp.IsSuccessStatusCode)
            {
                Plugin.Log.Warning($"[Faloop] Login HTTP {(int)loginResp.StatusCode} — falling back to anonymous");
                LastError = $"Login failed (HTTP {(int)loginResp.StatusCode}). Check credentials.";
                CacheSession(anonSession);
                return anonSession;
            }

            var loginJson = await loginResp.Content.ReadAsStringAsync(ct);
            using var lDoc = JsonDocument.Parse(loginJson);
            if (!lDoc.RootElement.TryGetProperty("success", out var lOk) || !lOk.GetBoolean())
            {
                Plugin.Log.Warning("[Faloop] Login success=false — falling back to anonymous");
                LastError = "Login failed. Check username/password.";
                CacheSession(anonSession);
                return anonSession;
            }

            var authedSession = lDoc.RootElement.GetProperty("data").GetProperty("sessionId").GetString();
            Plugin.Log.Information("[Faloop] Logged in successfully.");
            CacheSession(authedSession);
            return authedSession;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Faloop] Session fetch failed: {ex.Message} — will connect without one");
            return null;
        }
    }

    // Persist the latest session ID so we can resume it on next startup.
    private void CacheSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        if (_config.StoredSessionId == sessionId) return;   // unchanged
        _config.StoredSessionId = sessionId;
        try { _config.Save(); } catch { /* config save failure is non-fatal */ }
    }

    // ── Session receive loop ──────────────────────────────────────────

    private async Task RunSession(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[65_536];
        using var ms = new System.IO.MemoryStream(65_536);

        Volatile.Write(ref _lastRxTick, Environment.TickCount64);

        // C-1: liveness watchdog. A half-open TCP connection (NAT idle, Wi-Fi
        // drop, sleep/resume) never delivers FIN/RST, so ReceiveAsync would
        // block forever and the listener would die silently. Faloop's
        // Engine.IO pingInterval is ~25s; treat >60s of total silence as dead
        // and Abort() the socket — that makes the pending ReceiveAsync throw,
        // which ConnectLoop catches and turns into a backed-off reconnect.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (!idleCts.IsCancellationRequested)
                {
                    await Task.Delay(5000, idleCts.Token);
                    if (Environment.TickCount64 - Volatile.Read(ref _lastRxTick) > 60_000)
                    {
                        Plugin.Log.Warning("[Faloop] No frames for 60s — aborting dead socket.");
                        try { ws.Abort(); } catch { /* forces ReceiveAsync to throw */ }
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
        }, idleCts.Token);

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;

                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).ConfigureAwait(false);
                    Volatile.Write(ref _lastRxTick, Environment.TickCount64);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        SetState(ConnectionState.Disconnected);
                        return;
                    }

                    // S-3: accumulate raw bytes and decode ONCE at end-of-
                    // message. Decoding each fragment independently splits any
                    // multibyte UTF-8 sequence straddling a 64 KB boundary
                    // (JP reporter/mob names, the '·' separator) into mojibake.
                    ms.Write(buf, 0, result.Count);
                }
                while (!result.EndOfMessage);

                var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                await HandlePacket(text, ws, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            idleCts.Cancel();
            try { await watchdog.ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    // ── Protocol handling ─────────────────────────────────────────────

    private async Task HandlePacket(string packet, ClientWebSocket ws, CancellationToken ct)
    {
        if (packet.Length == 0) return;

        if (Plugin.Log.MinimumLogLevel <= Serilog.Events.LogEventLevel.Verbose)
            Plugin.Log.Verbose($"[Faloop] << {packet[..Math.Min(300, packet.Length)]}");

        switch (packet)
        {
            case "2probe":  // Engine.IO WebSocket probe from server
                await Send(ws, "3probe", ct);
                break;

            case "5":       // Engine.IO UPGRADE confirmed
                await Send(ws, SioConnectPacket(), ct);
                break;

            default:
                switch (packet[0])
                {
                    case '0':   // Engine.IO OPEN (direct WebSocket, no prior polling)
                        await Send(ws, SioConnectPacket(), ct);
                        break;

                    case '2':   // Engine.IO PING
                        await Send(ws, "3", ct);
                        break;

                    case '4' when packet.Length > 1:    // Engine.IO MESSAGE
                        await HandleSioPacket(packet[1..], ws, ct);
                        break;
                }
                break;
        }
    }

    // Builds Socket.IO CONNECT packet, including auth if we have a session ID
    private string SioConnectPacket() =>
        string.IsNullOrEmpty(_sessionId)
            ? "40"
            : $"40{{\"sessionid\":\"{_sessionId}\"}}";

    private async Task HandleSioPacket(string packet, ClientWebSocket ws, CancellationToken ct)
    {
        if (packet.Length == 0) return;

        switch (packet[0])
        {
            case '0':   // Socket.IO CONNECT confirmed by server
                LastError = null;
                SetState(ConnectionState.Connected);
                Plugin.Log.Information("[Faloop] Socket.IO connected.");
                await Send(ws, "42[\"ack\"]", ct);   // required by Faloop after connect
                break;

            case '1':   // Socket.IO DISCONNECT
                SetState(ConnectionState.Disconnected);
                break;

            case '2' when packet.Length > 1:    // Socket.IO EVENT
                ParseEvent(packet[1..]);
                break;

            case '4':   // Socket.IO ERROR
                LastError = packet.Length > 1 ? packet[1..] : "Unknown error";
                Plugin.Log.Warning($"[Faloop] Socket.IO error: {LastError}");
                break;
        }
    }

    // ── Event parsing ─────────────────────────────────────────────────

    private void ParseEvent(string json)
    {
        try
        {
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return;

            var eventName = root[0].GetString() ?? string.Empty;
            var payload   = root[1];

            if (!string.Equals(eventName, "message", StringComparison.OrdinalIgnoreCase)) return;

            var type    = GetString(payload, "type");
            var subType = GetString(payload, "subType");
            if (type != "mob" || subType != "report") return;

            if (!payload.TryGetProperty("data", out var mobData)) return;

            var action = GetString(mobData, "action");

            // JSON property name is "id" (not "ids")
            if (!mobData.TryGetProperty("id", out var ids)) return;

            var mobSlug   = GetString(ids, "mobId")   ?? string.Empty;
            var worldSlug = GetString(ids, "worldId") ?? string.Empty;
            var zoneInst  = ids.TryGetProperty("zoneInstance", out var zi) ? zi.GetInt32() : 0;

            // M-2: this is the global firehose (every world/DC). Build the log
            // string only when Debug is actually enabled — otherwise it's
            // hundreds of throwaway allocations/sec on the receive thread
            // during a spike, for nothing.
            if (Plugin.Log.MinimumLogLevel <= Serilog.Events.LogEventLevel.Debug)
                Plugin.Log.Debug($"[Faloop] message action={action} mob={mobSlug} world={worldSlug}");

            switch (action)
            {
                case "spawn":
                    HandleSpawnAction(mobSlug, worldSlug, zoneInst, mobData);
                    break;
                case "spawn_location":
                    HandleSpawnLocationAction(mobSlug, worldSlug, zoneInst, mobData);
                    break;
                case "spawn_release":
                    // Public-release event for scheduled/early-access spawns.
                    // Faloop emits this when the privileged-only pre-release
                    // window closes and the mob becomes visible to everyone.
                    // Without this handler, manual-release spawns never flip
                    // out of EARLY ACCESS — we just silently dropped the
                    // transition event.
                    HandleSpawnReleaseAction(mobSlug, worldSlug, zoneInst, mobData);
                    break;
                case "spawn_progress":
                    // Phase advancement for multi-phase SS-rank / precursor
                    // spawns. Updates Stage and narrows the marker cloud to
                    // the new phase's POI set — the "card flip" behaviour
                    // visible on Faloop's website as a hunt progresses.
                    HandleSpawnProgressAction(mobSlug, worldSlug, zoneInst, ids, mobData);
                    break;
                case "death":
                    HandleDeathAction(mobSlug, worldSlug, zoneInst, mobData);
                    break;
                case "spawn_false":
                    HandleSpawnFalseAction(mobSlug, worldSlug, zoneInst);
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Faloop] Event parse error: {ex.Message} | raw={json[..Math.Min(300, json.Length)]}");
        }
    }

    // ── Spawn / death handlers ────────────────────────────────────────

    private void HandleSpawnAction(string mobSlug, string worldSlug, int zoneInst, JsonElement mobData)
    {
        if (!FaloopData.Mobs.TryGetValue(mobSlug, out var mobInfo))
        {
            Plugin.Log.Warning($"[Faloop] Unknown mob slug: '{mobSlug}'");
            return;
        }

        // FATE bosses (Behemoth, Odin, Ixion, Daivadipa, etc.) aren't standard
        // hunt marks — drop them entirely.
        if (mobInfo.Rank == MobRank.FATE) return;

        var rank = mobInfo.Rank switch
        {
            MobRank.A => HuntRank.A,
            MobRank.B => HuntRank.B,
            _         => HuntRank.S,   // S and SS map to S
        };

        // Per-rank toggle + per-rank scope (DC / world whitelist). S also
        // covers SS (collapsed in the mapping above). B-rank tracking was
        // removed in v0.4.6 — any incoming B is dropped before the per-
        // rank scope lookup so we don't waste a dictionary access.
        if (rank == HuntRank.B) return;

        // ── Stage 1: filter ───────────────────────────────────────────
        FaloopData.Worlds.TryGetValue(worldSlug, out var worldId);
        if (!PassesRankScope(rank, worldId)) return;

        // The nested "data" object holds spawn-specific fields (Spawn record)
        if (!mobData.TryGetProperty("data", out var spawnData)) return;

        var zoneSlug = GetString(spawnData, "zoneId2") ?? string.Empty;
        FaloopData.TerritoryTypes.TryGetValue(zoneSlug, out var territoryId);
        if (!PassesExpansionFilter(territoryId)) return;

        // ── Stage 2: resolve ──────────────────────────────────────────
        var worldName = (worldId > 0 ? LookupWorldName(worldId) : null) ?? worldSlug;
        var (points, mapId, zonePoiId) = ResolveSpawnPoints(spawnData, zoneSlug, territoryId, mobSlug);

        float mapX = points.Count > 0 ? points[0].MapX : 0f;
        float mapY = points.Count > 0 ? points[0].MapY : 0f;

        var reporter = string.Empty;
        if (ArrayLen(spawnData, "reporters", out var reporters) > 0)
            reporter = GetString(reporters[0], "name") ?? string.Empty;

        var zoneName = (territoryId > 0 ? LookupZoneName(territoryId) : null) ?? zoneSlug;
        // Lumina's BNpcName is sentence-cased (e.g. "the Pale Rider"). Normalise
        // to proper Title Case ("The Pale Rider") for clean card/chat output.
        var mobName  = ToTitleCase(LookupMobName(mobInfo.BNpcId) ?? mobSlug);

        // Use Faloop's reported timestamp (ISO 8601 in UTC) so "Age" reflects
        // actual time-since-spawn, not time-since-we-saw-the-event.
        var reportedAt = TryParseUtc(GetString(spawnData, "timestamp")) ?? DateTime.Now;

        // Parse top-level scheduled / stage / scheduleDelay once. We have to
        // know `wentPublic` BEFORE constructing the SpawnInfo so JustWentPublic
        // and PublicReleasedAt are set at construction (record is init-only
        // post-C-1 — no more in-place mutation after the slot replace).
        var isScheduled  = GetBool(spawnData, "isScheduled");
        var scheduleDelay = spawnData.TryGetProperty("scheduleDelay", out var sd) &&
                            sd.ValueKind == JsonValueKind.Number &&
                            sd.TryGetInt32(out var sdv) ? (int?)sdv : null;
        var stage         = spawnData.TryGetProperty("stage", out var st) &&
                            st.ValueKind == JsonValueKind.Number &&
                            st.TryGetInt32(out var stv) ? (int?)stv : null;

        // S-2: Faloop legitimately re-emits `spawn` for the same mark, and a
        // reconnect can replay recent events. Without an identity key that
        // produced duplicate, un-killable cards (death/location only patch the
        // first match). Upsert on (mob, world, instance) while still alive, and
        // only fire the new-spawn alert for a genuinely new entry.
        SpawnInfo spawn;
        bool isNew;
        lock (_lock)
        {
            // H-1: upsert identity keyed on wire slugs, not resolved names.
            var idx = FindSpawnIndexLocked(mobSlug, worldSlug, zoneInst);

            bool wentPublic      = false;
            bool coordsRevealed  = false;
            bool coordsCorrected = false;
            DateTime? releasedStamp = null;
            if (idx >= 0)
            {
                // Watcher for the pre-release/early-access → public transition.
                // Compute *before* constructing the SpawnInfo so the JustWent-
                // Public flag is set immutably at construction.
                var prev = _spawns[idx];
                wentPublic = prev.IsScheduled && !isScheduled;
                releasedStamp = wentPublic ? TimeSync.ServerNow : prev.PublicReleasedAt;

                // Detect coordless→coords transition. The initial echo (when
                // the spawn first arrived without coords) only printed the
                // prefix line — no clickable map flag. Re-firing OnNewSpawn
                // here gives the user a proper flag echo once Faloop tells
                // us where the mob actually is.
                var prevHadCoords = prev.X > 0 || prev.Y > 0;
                var newHasCoords  = mapX > 0 || mapY > 0;
                coordsRevealed  = !prevHadCoords && newHasCoords;
                // Real→real correction, debounced by distance so a reporter
                // nudging the marker a fraction of a tile stays quiet.
                coordsCorrected = prevHadCoords && newHasCoords &&
                                  MapDist(prev.X, prev.Y, mapX, mapY) > CoordCorrectionThreshold;

                Plugin.Log.Debug(
                    $"[Faloop] Refresh {mobName}@{worldName} i{zoneInst}: " +
                    $"prev.scheduled={prev.IsScheduled} stage={prev.Stage?.ToString() ?? "null"} → " +
                    $"new.scheduled={isScheduled} stage={stage?.ToString() ?? "null"} " +
                    $"(wentPublic={wentPublic}, coordsRevealed={coordsRevealed}, coordsCorrected={coordsCorrected})");
            }

            spawn = new SpawnInfo
            {
                MobSlug          = mobSlug,
                WorldSlug        = worldSlug,
                World            = worldName,
                MobName          = mobName,
                ZoneName         = zoneName,
                Rank             = rank,
                HpPercent        = 100,
                Reporter         = reporter,
                ReportedAt       = reportedAt,
                ZoneInstance     = zoneInst,
                TerritoryId      = territoryId,
                MapId            = mapId,
                ZonePoiId        = zonePoiId,
                Points           = points,
                IsSS             = mobInfo.Rank == MobRank.SS,
                IsScheduled      = isScheduled,
                ScheduleDelay    = scheduleDelay,
                Stage            = stage,
                JustWentPublic   = wentPublic,
                CoordsRevealed   = coordsRevealed,
                CoordsCorrected  = coordsCorrected,
                PublicReleasedAt = releasedStamp,
                RawEvent         = mobData.GetRawText(),
            };

            if (idx >= 0)
            {
                if (coordsRevealed)
                    Plugin.Log.Information(
                        $"[Faloop] Upsert {mobName}@{worldName} i{zoneInst} " +
                        $"gained coords (0,0)→({mapX:F1},{mapY:F1}) " +
                        $"points={points.Count} wentPublic={wentPublic}");

                _spawns[idx] = spawn;   // atomic slot replace — readers never see a torn state
                // Re-fire OnNewSpawn for any user-visible location change:
                // scheduled→public, coordless→coords, or a debounced real→
                // real correction. All warrant a fresh chat echo + sound.
                isNew = wentPublic || coordsRevealed || coordsCorrected;
            }
            else
            {
                // Diagnostic: a brand-new card for a mob/world/instance we
                // had nothing for. If reports suggest "the pre-release card
                // didn't update on release", this log line + the previous
                // pre-release's match key reveal whether the upsert key
                // diverged (different MobName casing, different instance #,
                // etc.) and we ended up with two cards instead of one.
                Plugin.Log.Debug(
                    $"[Faloop] New card {mobName}@{worldName} i{zoneInst} " +
                    $"scheduled={isScheduled} stage={stage?.ToString() ?? "null"}");

                _spawns.Insert(0, spawn);
                while (_spawns.Count > _config.MaxEntries)
                    _spawns.RemoveAt(_spawns.Count - 1);
                isNew = true;
            }
            RebuildSnapshotLocked();
        }

        if (isNew) OnNewSpawn?.Invoke(spawn);   // don't re-alert on a refresh
        OnUpdate?.Invoke();
    }

    // ── HandleSpawnAction pipeline stages (M-2, v0.4.14 review) ───────
    // The spawn handler used to be a ~200-line monolith doing filtering,
    // parsing, coordinate math, transition detection, and upsert in one
    // method — every recent bug required surgery inside it. Each stage now
    // has one job and one reason to change.

    // Per-rank scope filter: rank toggle → data-center → world whitelist.
    // S-rank events filter against the S scope, A-rank against the A scope.
    // Empty/"All" DC disables the DC filter; a disabled world filter keeps
    // the whole DC.
    private bool PassesRankScope(HuntRank rank, uint worldId)
    {
        var rankAllowed = rank == HuntRank.S ? _config.ShowSRanks : _config.ShowARanks;
        if (!rankAllowed) return false;

        var scopeDc     = rank == HuntRank.S ? _config.SDataCenter         : _config.ADataCenter;
        var scopeWfOn   = rank == HuntRank.S ? _config.SWorldFilterEnabled : _config.AWorldFilterEnabled;
        var scopeWfList = rank == HuntRank.S ? _config.SWorldWhitelist     : _config.AWorldWhitelist;

        if (!string.IsNullOrEmpty(scopeDc) &&
            !scopeDc.Equals("All", StringComparison.OrdinalIgnoreCase) &&
            FaloopData.DataCenters.TryGetValue(scopeDc, out var dcWorlds) &&
            !dcWorlds.Contains(worldId))
            return false;

        if (scopeWfOn && !scopeWfList.Contains((int)worldId))
            return false;

        return true;
    }

    // Per-expansion filter (e.g. "only Dawntrail"). Only applies when
    // enabled. A spawn whose territory we can't classify (unknown zone) is
    // never dropped here — better to show a slightly-mislabelled card than
    // to silently swallow a real S-rank.
    private bool PassesExpansionFilter(uint territoryId)
    {
        if (!_config.ExpansionFilterEnabled) return true;
        var exp = FaloopData.ExpansionForTerritory(territoryId);
        return !exp.HasValue || _config.ExpansionWhitelist.Contains((int)exp.Value);
    }

    // Resolve the event's reported position(s) into map-ready SpawnPoints.
    // A normal spawn has a precise "location" or a single zonePoiId; SS
    // "minion" reports carry several zonePoiIds at once — collect every one
    // so each gets a map marker AND its own clickable chat flag. A precise
    // location supersedes the POI cloud.
    private static (List<SpawnPoint> Points, uint MapId, int ZonePoiId) ResolveSpawnPoints(
        JsonElement spawnData, string zoneSlug, uint territoryId, string mobSlug)
    {
        var rawPts = new List<(int X, int Y)>();
        string? locationStr = GetString(spawnData, "location");
        int zonePoiId = 0;

        if (ArrayLen(spawnData, "zonePoiIds", out var pois) > 0)
        {
            if (pois[0].ValueKind == JsonValueKind.Number) zonePoiId = pois[0].GetInt32();
            if (locationStr == null)
                foreach (var pe in pois.EnumerateArray())
                {
                    if (pe.ValueKind != JsonValueKind.Number) continue;
                    var poiId = pe.GetInt32();
                    if (FaloopData.Locations.TryGetValue(poiId, out var ploc) &&
                        TryParseRaw(ploc, out var px, out var py))
                        rawPts.Add((px, py));
                    else
                        // Loud warning: a missing POI is a data-gap bug, not a
                        // runtime condition. The card will render markerless
                        // and we want to know which IDs need to be added.
                        Plugin.Log.Warning(
                            $"[Faloop] Unknown zonePoiId {poiId} in zone " +
                            $"'{zoneSlug}' (mob {mobSlug}). Add to faloop-data.json.");
                }
        }

        if (locationStr != null && TryParseRaw(locationStr, out var lx, out var ly))
        {
            rawPts.Clear();
            rawPts.Add((lx, ly));
        }

        // Convert each raw point → in-game map coords (the clickable chat
        // flags need map coords; the thumbnail needs the raw 2048 ones).
        var points = new List<SpawnPoint>();
        uint mapId = 0;

        if (territoryId > 0)
        {
            var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            if (sheet != null && sheet.TryGetRow(territoryId, out var tt))
            {
                mapId = tt.Map.ValueNullable?.RowId ?? 0;
                foreach (var rp in rawPts)
                {
                    var c = ResolveCoords(territoryId, $"{rp.X},{rp.Y}");
                    if (c.HasValue)
                        points.Add(new SpawnPoint(c.Value.rawX, c.Value.rawY,
                                                  c.Value.x, c.Value.y));
                }
            }
        }

        return (points, mapId, zonePoiId);
    }

    private void HandleSpawnLocationAction(string mobSlug, string worldSlug, int zoneInst, JsonElement mobData)
    {
        if (!FaloopData.Mobs.TryGetValue(mobSlug, out var mobInfo)) return;
        var mobName = LookupMobName(mobInfo.BNpcId) ?? mobSlug;

        FaloopData.Worlds.TryGetValue(worldSlug, out var worldId);
        var worldName = (worldId > 0 ? LookupWorldName(worldId) : null) ?? worldSlug;

        if (!mobData.TryGetProperty("data", out var locData)) return;

        // SpawnLocation payload: {"zonePoiId": 643, "location": "1220,740"}
        string? locationStr = GetString(locData, "location");
        if (locationStr == null && locData.TryGetProperty("zonePoiId", out var poiEl) &&
            poiEl.ValueKind == JsonValueKind.Number)
        {
            FaloopData.Locations.TryGetValue(poiEl.GetInt32(), out locationStr);
        }
        if (string.IsNullOrEmpty(locationStr)) return;

        // C-1 fix: atomic slot replacement instead of in-place mutation. We
        // hold _lock for the index lookup, the ResolveCoords call (which only
        // reads Lumina), and the slot swap — so the render thread either sees
        // the pre-refinement SpawnInfo or the post-refinement one, never a
        // half-constructed state. Old code mutated target.X/Y/Points while
        // the renderer could be enumerating spawn.Points — a List<T>
        // GetEnumerator violation waiting to happen.
        SpawnInfo? next = null;
        var coordsRevealed  = false;
        var coordsCorrected = false;
        lock (_lock)
        {
            // H-1: slug-keyed lookup. aliveOnly:false preserves the old
            // behaviour of patching dead entries' locations too.
            var idx = FindSpawnIndexLocked(mobSlug, worldSlug, zoneInst, aliveOnly: false);
            if (idx < 0) return;

            var prev   = _spawns[idx];
            var coords = ResolveCoords(prev.TerritoryId, locationStr);
            if (coords == null) return;

            // Coordless→coords reveal, or a debounced real→real correction.
            // Both fire the OnNewSpawn re-echo below; the prefix (chosen in
            // Plugin.HandleNewSpawn) distinguishes "[Location]" (reveal) from
            // "[Location updated]" (correction).
            var prevHadCoords = prev.X > 0 || prev.Y > 0;
            coordsRevealed  = !prevHadCoords;
            coordsCorrected = prevHadCoords &&
                              MapDist(prev.X, prev.Y, coords.Value.x, coords.Value.y) > CoordCorrectionThreshold;

            // A precise location collapses the POI cloud to one exact point.
            var pt = new SpawnPoint(coords.Value.rawX, coords.Value.rawY,
                                    coords.Value.x,   coords.Value.y);
            // Points is the single source of truth (M-3) — X/Y/RawX/RawY are
            // computed from Points[0], so replacing Points IS the coord update.
            next = prev with
            {
                Points          = new[] { pt },
                CoordsRevealed  = coordsRevealed,
                CoordsCorrected = coordsCorrected,
            };
            _spawns[idx] = next;
            RebuildSnapshotLocked();
        }
        Plugin.Log.Debug($"[Faloop] Updated location for {mobName} on {worldName}: ({next.X:F1}, {next.Y:F1}) (coordsRevealed={coordsRevealed}, coordsCorrected={coordsCorrected})");

        if (coordsRevealed || coordsCorrected) OnNewSpawn?.Invoke(next);
        OnUpdate?.Invoke();
    }

    // Convert Faloop's raw 2048-scale "x,y" string to in-game map coords using
    // the territory's map SizeFactor. Returns null if anything fails.
    // Parse Faloop's raw 2048-scale "x,y" string into ints.
    private static bool TryParseRaw(string s, out int x, out int y)
    {
        x = y = 0;
        if (string.IsNullOrEmpty(s)) return false;
        var p = s.Split(',');
        return p.Length == 2 && int.TryParse(p[0], out x) && int.TryParse(p[1], out y);
    }

    // Euclidean distance between two in-game map coordinates, in map-units.
    // Used to debounce coordinate corrections (only a move beyond the
    // CoordCorrectionThreshold re-echoes).
    private static float MapDist(float x1, float y1, float x2, float y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static (float x, float y, int rawX, int rawY)? ResolveCoords(uint territoryId, string locationStr)
    {
        if (territoryId == 0 || string.IsNullOrEmpty(locationStr)) return null;

        var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
        if (sheet == null || !sheet.TryGetRow(territoryId, out var tt)) return null;

        var map = tt.Map.ValueNullable;
        if (!map.HasValue) return null;

        var parts = locationStr.Split(',');
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out var rawX) ||
            !int.TryParse(parts[1], out var rawY)) return null;

        // m-1 fix (v0.4.7 audit): guard against SizeFactor == 0 (would produce
        // Infinity coords and render markers off-canvas). Real Lumina rows
        // always have a non-zero SizeFactor, but a stale/incomplete sheet on
        // first load briefly returns zero defaults.
        if (map.Value.SizeFactor == 0) return null;
        var n = 41.0f / (map.Value.SizeFactor / 100.0f);
        return ((float)(rawX / 2048.0 * n + 1), (float)(rawY / 2048.0 * n + 1), rawX, rawY);
    }

    private void HandleDeathAction(string mobSlug, string worldSlug, int zoneInst, JsonElement mobData)
    {
        if (!FaloopData.Mobs.TryGetValue(mobSlug, out var mobInfo)) return;
        var mobName = LookupMobName(mobInfo.BNpcId) ?? mobSlug;

        FaloopData.Worlds.TryGetValue(worldSlug, out var worldId);
        var worldName = (worldId > 0 ? LookupWorldName(worldId) : null) ?? worldSlug;

        // Death payload: {"action":"death","data":{"startedAt":"2026-...Z"}}
        DateTime killedAt = DateTime.Now;
        if (mobData.TryGetProperty("data", out var deathData))
            killedAt = TryParseUtc(GetString(deathData, "startedAt")) ?? DateTime.Now;

        // C-1 fix: replace, don't mutate. See HandleSpawnLocationAction.
        lock (_lock)
        {
            // H-1: slug-keyed lookup (matches dead entries too — a replayed
            // death event for an already-dead card is a harmless no-op patch).
            var idx = FindSpawnIndexLocked(mobSlug, worldSlug, zoneInst, aliveOnly: false);
            if (idx < 0) return;

            _spawns[idx] = _spawns[idx] with { IsDead = true, KilledAt = killedAt };
            RebuildSnapshotLocked();
        }

        OnUpdate?.Invoke();
    }

    // Parse an ISO 8601 string (with optional Z) and return it as a local DateTime.
    private static DateTime? TryParseUtc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var utc))
            return utc.ToLocalTime();
        return null;
    }

    // spawn_release: Faloop's signal that a previously-scheduled (pre-release
    // or early-access) spawn just became publicly visible. Find the existing
    // tracked card, flip IsScheduled → false, stamp PublicReleasedAt so the
    // renderer shows JUST RELEASED, and re-fire OnNewSpawn so the user gets
    // a fresh ding + "[Public release]" echo at the moment that actually
    // matters for pulling. Decoded from Faloop's main.js: fK = (e, t) =>
    // ({ ...t.spawn, isScheduled: false, timestamp: e.timestamp })
    private void HandleSpawnReleaseAction(string mobSlug, string worldSlug, int zoneInst, JsonElement mobData)
    {
        if (!FaloopData.Mobs.TryGetValue(mobSlug, out var mobInfo)) return;
        var mobName = LookupMobName(mobInfo.BNpcId) ?? mobSlug;

        FaloopData.Worlds.TryGetValue(worldSlug, out var worldId);
        var worldName = (worldId > 0 ? LookupWorldName(worldId) : null) ?? worldSlug;

        // Faloop uses the release event's timestamp as the new ReportedAt
        // (matches fK's `timestamp: new Date(e.timestamp).toISOString()`).
        // Defensive (v0.4.8.2): also parse `location` / `zonePoiIds` if the
        // release payload includes them. The bundle's fK decoder ignores
        // these, but the wire payload may differ — and a pre-release card
        // that had no POIs would otherwise stay markerless even after the
        // release event fires.
        DateTime? releaseTime = null;
        string? releaseLocationStr = null;
        var releasePoiIds = new List<int>();
        if (mobData.TryGetProperty("data", out var rData))
        {
            releaseTime        = TryParseUtc(GetString(rData, "timestamp"));
            releaseLocationStr = GetString(rData, "location");
            if (rData.TryGetProperty("zonePoiIds", out var pois) &&
                pois.ValueKind == JsonValueKind.Array)
            {
                foreach (var pe in pois.EnumerateArray())
                    if (pe.ValueKind == JsonValueKind.Number) releasePoiIds.Add(pe.GetInt32());
            }
        }

        SpawnInfo? released = null;
        lock (_lock)
        {
            // H-1: slug-keyed lookup.
            var idx = FindSpawnIndexLocked(mobSlug, worldSlug, zoneInst);

            if (idx < 0)
            {
                // No card to flip — release for a spawn we never saw the
                // pre-release of. Nothing useful to do here; ignore.
                Plugin.Log.Debug($"[Faloop] spawn_release for unknown {mobName}@{worldName} i{zoneInst}");
                return;
            }

            var prev = _spawns[idx];

            // Compute effective points: prefer release-event-derived if the
            // payload included them, fall back to prev otherwise. Resolved
            // through the same path as HandleSpawnAction so single-point
            // (`location`) and POI-cloud (`zonePoiIds`) both work. Points is
            // the single source of truth (M-3) — X/Y/RawX/RawY are computed
            // from Points[0], so swapping the list IS the coord update.
            var effPoiId = prev.ZonePoiId;
            IReadOnlyList<SpawnPoint> effPoints = prev.Points;

            var freshRaw = new List<(int X, int Y)>();
            if (!string.IsNullOrEmpty(releaseLocationStr) &&
                TryParseRaw(releaseLocationStr, out var lx, out var ly))
            {
                freshRaw.Add((lx, ly));
            }
            else
            {
                foreach (var pid in releasePoiIds)
                    if (FaloopData.Locations.TryGetValue(pid, out var ploc) &&
                        TryParseRaw(ploc, out var px, out var py))
                        freshRaw.Add((px, py));
            }

            if (freshRaw.Count > 0 && prev.TerritoryId > 0)
            {
                var built = new List<SpawnPoint>();
                foreach (var rp in freshRaw)
                {
                    var c = ResolveCoords(prev.TerritoryId, $"{rp.X},{rp.Y}");
                    if (c.HasValue)
                        built.Add(new SpawnPoint(c.Value.rawX, c.Value.rawY, c.Value.x, c.Value.y));
                }
                if (built.Count > 0)
                {
                    effPoints = built;
                    if (releasePoiIds.Count > 0) effPoiId = releasePoiIds[0];

                    if (prev.X == 0 && prev.Y == 0)
                        Plugin.Log.Information(
                            $"[Faloop] spawn_release {mobName}@{worldName} i{zoneInst} " +
                            $"gained coords from release event: ({built[0].MapX:F1},{built[0].MapY:F1})");
                }
            }

            // Build the released record by cloning the previous one and
            // flipping the scheduled state. Coords are either freshly
            // resolved from the release event or preserved from prev.
            // CoordsRevealed fires in the case where prev was at (0,0)
            // and the release event itself carried coords — the echo
            // re-fire below already runs unconditionally for releases
            // (JustWentPublic), but flagging CoordsRevealed lets the
            // chat prefix accurately credit the location update too.
            var releaseCoordsRevealed = prev.X == 0 && prev.Y == 0 &&
                                        effPoints.Count > 0 &&
                                        (effPoints[0].MapX > 0 || effPoints[0].MapY > 0);
            released = prev with
            {
                ReportedAt       = releaseTime ?? DateTime.Now,
                RawEvent         = mobData.GetRawText(),
                ZonePoiId        = effPoiId,
                Points           = effPoints,
                IsScheduled      = false,           // ← the flip
                ScheduleDelay    = null,
                Stage            = null,
                JustWentPublic   = true,
                CoordsRevealed   = releaseCoordsRevealed,
                CoordsCorrected  = false,   // transient — don't carry a stale flag through the clone
                PublicReleasedAt = TimeSync.ServerNow,
                IsDead           = false,
                KilledAt         = null,
            };

            _spawns[idx] = released;
            RebuildSnapshotLocked();
        }

        Plugin.Log.Debug($"[Faloop] spawn_release fired for {mobName}@{worldName} i{zoneInst}");
        OnNewSpawn?.Invoke(released);   // re-ding + [Public release] echo
        OnUpdate?.Invoke();
    }

    // spawn_progress: a multi-phase SS-precursor / SS-rank spawn just advanced
    // to a new phase. The matching MobData.Phases entry tells us which POIs
    // are active in this new phase. We narrow the card's marker cloud to the
    // intersection of (this phase's POIs) ∩ (POIs in the spawn's current
    // zone), so a 24-POI Phase 1 cloud collapses to the 4-POI Phase 2
    // cluster, then to the 1-POI Phase 3 final spot — same visual narrowing
    // Faloop's own site does. Decoded from main.js: gK = (e, t, n) => ({
    //   ...n.spawn, stage: e.phaseNum, zonePoiIds: u when (u.length===1 ||
    //   phase.grouped), timestamp: ... })
    private void HandleSpawnProgressAction(string mobSlug, string worldSlug, int zoneInst,
                                            JsonElement ids, JsonElement mobData)
    {
        if (!FaloopData.Mobs.TryGetValue(mobSlug, out var mobInfo)) return;
        if (mobInfo.Phases == null || mobInfo.Phases.Length == 0) return;

        var mobName = LookupMobName(mobInfo.BNpcId) ?? mobSlug;
        FaloopData.Worlds.TryGetValue(worldSlug, out var worldId);
        var worldName = (worldId > 0 ? LookupWorldName(worldId) : null) ?? worldSlug;

        // phaseNum is 1-based on the wire; clamp to the Phases array bounds.
        var phaseNum = ids.TryGetProperty("phaseNum", out var pn) && pn.TryGetInt32(out var pv) ? pv : 1;
        if (phaseNum < 1 || phaseNum > mobInfo.Phases.Length) return;
        var phase = mobInfo.Phases[phaseNum - 1];

        // event's data block for the new timestamp.
        DateTime? progressTime = null;
        if (mobData.TryGetProperty("data", out var pData))
            progressTime = TryParseUtc(GetString(pData, "timestamp"));

        SpawnInfo? next = null;
        lock (_lock)
        {
            // H-1: slug-keyed lookup.
            var idx = FindSpawnIndexLocked(mobSlug, worldSlug, zoneInst);

            if (idx < 0)
            {
                Plugin.Log.Debug($"[Faloop] spawn_progress for unknown {mobName}@{worldName} i{zoneInst} ph{phaseNum}");
                return;
            }

            var prev = _spawns[idx];

            // Cross-zone phase POI filter. Some phases (notably arch_aethereater
            // phase 2) list one POI per zone the SS can spawn in. The naive
            // narrowing — "include every POI in the phase, convert all with
            // the spawn's territory SizeFactor" — produced one marker per zone
            // all rendered in the spawn's own zone's coordinate space. Each
            // POI's owning zone is now in FaloopData.PoiZones (extracted from
            // Faloop's Zone.pois definitions); we drop any POI whose owning
            // zone isn't the spawn's zone, leaving exactly the POIs that
            // could plausibly contain the mob.
            var spawnZoneSlug = FaloopData.SlugForTerritory(prev.TerritoryId);
            var narrowedRaw = new List<(int X, int Y)>();
            var narrowedPois = new List<int>();
            foreach (var poiId in phase.ZonePoiIds)
            {
                // Drop POIs from other zones up-front so we don't waste a
                // ResolveCoords call and don't produce ghost markers.
                if (spawnZoneSlug != null &&
                    FaloopData.PoiZones.TryGetValue(poiId, out var poiZone) &&
                    !string.Equals(poiZone, spawnZoneSlug, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (FaloopData.Locations.TryGetValue(poiId, out var ploc) &&
                    TryParseRaw(ploc, out var px, out var py))
                {
                    narrowedRaw.Add((px, py));
                    narrowedPois.Add(poiId);
                }
            }

            // Convert raw → map coords using the spawn's existing territory.
            var built = new List<SpawnPoint>();
            if (prev.TerritoryId > 0)
            {
                foreach (var rp in narrowedRaw)
                {
                    var c = ResolveCoords(prev.TerritoryId, $"{rp.X},{rp.Y}");
                    if (c.HasValue)
                        built.Add(new SpawnPoint(c.Value.rawX, c.Value.rawY,
                                                 c.Value.x, c.Value.y));
                }
            }

            // If filtering knocked everything out (e.g. PoiZones table missing
            // entries for this mob's POIs), keep the previous markers rather
            // than show an empty card.
            IReadOnlyList<SpawnPoint> points = built.Count == 0
                ? prev.Points
                : built;

            // `with`-clone so identity fields (MobSlug/WorldSlug, H-1) and any
            // future additions carry over automatically instead of each full
            // initializer needing to remember them.
            // Points drives X/Y/RawX/RawY (computed, M-3); `points` here is
            // either the narrowed phase set or prev.Points, so the coords
            // follow automatically.
            next = prev with
            {
                ReportedAt       = progressTime ?? prev.ReportedAt,
                RawEvent         = mobData.GetRawText(),
                ZonePoiId        = narrowedPois.Count > 0 ? narrowedPois[0] : prev.ZonePoiId,
                Points           = points,
                Stage            = phaseNum,
                JustWentPublic   = false,
                CoordsRevealed   = false,
                CoordsCorrected  = false,
                IsDead           = false,
                KilledAt         = null,
            };

            _spawns[idx] = next;
            RebuildSnapshotLocked();
        }

        Plugin.Log.Debug($"[Faloop] spawn_progress {mobName}@{worldName} i{zoneInst} → phase {phaseNum} ({next.Points.Count} pts, grouped={phase.Grouped})");
        OnUpdate?.Invoke();
    }

    private void HandleSpawnFalseAction(string mobSlug, string worldSlug, int zoneInst)
    {
        if (!FaloopData.Mobs.TryGetValue(mobSlug, out var mobInfo)) return;
        var mobName = LookupMobName(mobInfo.BNpcId) ?? mobSlug;

        FaloopData.Worlds.TryGetValue(worldSlug, out var worldId);
        var worldName = (worldId > 0 ? LookupWorldName(worldId) : null) ?? worldSlug;

        lock (_lock)
        {
            // H-1: slug-keyed removal.
            _spawns.RemoveAll(s =>
                string.Equals(s.MobSlug,   mobSlug,   StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.WorldSlug, worldSlug, StringComparison.OrdinalIgnoreCase) &&
                s.ZoneInstance == zoneInst);
            RebuildSnapshotLocked();
        }

        OnUpdate?.Invoke();
    }

    // ── Lumina lookups ────────────────────────────────────────────────

    private static string? LookupWorldName(uint id)
    {
        if (id == 0) return null;
        var sheet = Plugin.DataManager.GetExcelSheet<World>();
        if (sheet == null) return null;
        return sheet.TryGetRow(id, out var row) ? row.Name.ToString() : null;
    }

    private static string? LookupMobName(uint id)
    {
        if (id == 0) return null;
        var sheet = Plugin.DataManager.GetExcelSheet<BNpcName>();
        if (sheet == null) return null;
        return sheet.TryGetRow(id, out var row) ? row.Singular.ToString() : null;
    }

    private static string? LookupZoneName(uint id)
    {
        if (id == 0) return null;
        var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
        if (sheet == null) return null;
        if (!sheet.TryGetRow(id, out var row)) return null;
        return row.PlaceName.ValueNullable?.Name.ToString();
    }

    // ── JSON helpers ──────────────────────────────────────────────────

    // "the Pale Rider" / "HELLSCLAW" → "The Pale Rider" / "Hellsclaw".
    private static string ToTitleCase(string s) =>
        string.IsNullOrEmpty(s)
            ? s
            : System.Globalization.CultureInfo.InvariantCulture.TextInfo
                .ToTitleCase(s.ToLowerInvariant());

    private static string? GetString(JsonElement e, string key) =>
        e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool GetBool(JsonElement e, string key) =>
        e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;

    // H-2 (v0.4.14 review): element count of `key` iff it exists AND is an
    // array; 0 otherwise. TryGetProperty alone guards *presence* — a JSON
    // null (`"reporters": null`) would still reach GetArrayLength() and
    // throw InvalidOperationException, which ParseEvent's catch would turn
    // into a silently dropped spawn event. A dropped S-rank notification is
    // the worst failure mode this plugin has, so tolerate every shape.
    private static int ArrayLen(JsonElement e, string key, out JsonElement arr)
    {
        if (e.TryGetProperty(key, out arr) && arr.ValueKind == JsonValueKind.Array)
            return arr.GetArrayLength();
        arr = default;
        return 0;
    }

    // H-1 (v0.4.14 review): the ONE spawn-identity predicate, keyed on wire
    // slugs (never on Lumina-resolved display names — see SpawnInfo.MobSlug).
    // Must be called inside `lock (_lock)`. aliveOnly=false matches dead
    // entries too (death/location handlers patch those).
    private int FindSpawnIndexLocked(string mobSlug, string worldSlug, int zoneInst, bool aliveOnly = true)
        => _spawns.FindIndex(s =>
            (!aliveOnly || !s.IsDead) &&
            string.Equals(s.MobSlug,   mobSlug,   StringComparison.OrdinalIgnoreCase) &&
            string.Equals(s.WorldSlug, worldSlug, StringComparison.OrdinalIgnoreCase) &&
            s.ZoneInstance == zoneInst);

    // ── Send helper ───────────────────────────────────────────────────

    private static async Task Send(ClientWebSocket ws, string msg, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(msg);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        Plugin.Log.Verbose($"[Faloop] >> {msg}");
    }

    // ── Misc ──────────────────────────────────────────────────────────

    private void SetState(ConnectionState s)
    {
        State = s;
        OnUpdate?.Invoke();
    }

    public void Dispose()
    {
        // M-1 fix (v0.4.7 audit): never block the framework thread on
        // _loopTask.Wait(). Dalamud calls Dispose on the framework thread —
        // a synchronous Wait both stalls the game (up to 2s) and risks a
        // hard deadlock if any future continuation in the loop tries to
        // RunOnFrameworkThread. Cancel and let the loop unwind asynchronously;
        // dispose the CTS from a fire-and-forget continuation on the thread
        // pool so it can never be touched after Dispose returns.
        var cts  = _cts;
        var task = _loopTask ?? Task.CompletedTask;
        var http = _http;
        try { cts.Cancel(); } catch { /* already disposed */ }
        _ = task.ContinueWith(_ =>
        {
            try { cts.Dispose(); }  catch { /* already disposed */ }
            try { http.Dispose(); } catch { /* already disposed */ }
        }, TaskScheduler.Default);
    }
}
