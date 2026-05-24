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

    private readonly Configuration   _config;
    private readonly List<SpawnInfo> _spawns = new();
    private readonly object          _lock   = new();
    private readonly object          _lifecycle = new();
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

                _sessionId = await FetchAnonSession(ct);

                Plugin.Log.Information($"[Faloop] Connecting to {wsUrl}");

                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Origin",     FaloopOrigin);
                ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 Edg/122.0.0.0");

                await ws.ConnectAsync(wsUri, ct);

                connectedAt = DateTime.UtcNow;
                timeSyncTask ??= TimeSyncLoop(timeSyncCts.Token);

                await RunSession(ws, ct);
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
    private static async Task TimeSyncLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(30), ct);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
                using var req  = new HttpRequestMessage(HttpMethod.Head, FaloopBase);
                using var resp = await http.SendAsync(req, ct);
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
            using var http = MakeBrowserHttpClient(FaloopBase);

            // Step 1: refresh — pass the previously cached session ID so the
            // server can resume it if still valid (saves a roundtrip and
            // reduces auth churn). The server returns a fresh ID if the cached
            // one expired or was never set.
            var cachedSession = string.IsNullOrWhiteSpace(_config.StoredSessionId)
                ? "null"
                : $"\"{_config.StoredSessionId}\"";
            var refreshBody = $"{{\"sessionId\":{cachedSession}}}";
            using var refreshContent = new StringContent(refreshBody, Encoding.UTF8, "application/json");
            using var refreshResp = await http.PostAsync(ApiRefresh, refreshContent, ct);
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

            // Step 2: login — uses anon sessionId + JWT to authenticate
            using var loginHttp = MakeBrowserHttpClient(FaloopLoginRef);
            // The token starts with "JWT " — pass through unvalidated (default
            // AuthenticationHeaderValue parser rejects schemes containing dots).
            loginHttp.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", token);

            var loginPayload = JsonSerializer.Serialize(new
            {
                username   = cfgUser,
                password   = cfgPass,
                rememberMe = false,
                sessionId  = anonSession,
            });
            using var loginContent = new StringContent(loginPayload, Encoding.UTF8, "application/json");
            using var loginResp = await loginHttp.PostAsync(ApiLogin, loginContent, ct);
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

    private static HttpClient MakeBrowserHttpClient(string referer)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Add("Accept",          "application/json, text/plain, */*");
        http.DefaultRequestHeaders.Add("Accept-Language", "en");
        http.DefaultRequestHeaders.Add("Origin",          FaloopOrigin);
        http.DefaultRequestHeaders.Add("Referer",         referer);
        http.DefaultRequestHeaders.Add("User-Agent",      "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 Edg/122.0.0.0");
        return http;
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
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
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
                await HandlePacket(text, ws, ct);
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

        if (_config.OnlySRanks && rank != HuntRank.S) return;

        // Resolve world name via Lumina
        FaloopData.Worlds.TryGetValue(worldSlug, out var worldId);
        var worldName = (worldId > 0 ? LookupWorldName(worldId) : null) ?? worldSlug;

        // Data center filter
        if (!string.IsNullOrEmpty(_config.DataCenter) &&
            !_config.DataCenter.Equals("All", StringComparison.OrdinalIgnoreCase) &&
            FaloopData.DataCenters.TryGetValue(_config.DataCenter, out var dcWorlds) &&
            !dcWorlds.Contains(worldId))
        {
            return;
        }

        // Per-world filter (a subset of the DC the user explicitly ticked).
        // Only applies when enabled; the empty/disabled case keeps the full DC.
        if (_config.WorldFilterEnabled && !_config.WorldWhitelist.Contains((int)worldId))
            return;

        // The nested "data" object holds spawn-specific fields (Spawn record)
        if (!mobData.TryGetProperty("data", out var spawnData)) return;

        var zoneSlug = GetString(spawnData, "zoneId2") ?? string.Empty;
        FaloopData.TerritoryTypes.TryGetValue(zoneSlug, out var territoryId);

        // Per-expansion filter (e.g. "only Dawntrail"). Only applies when
        // enabled. A spawn whose territory we can't classify (unknown zone) is
        // never dropped here — better to show a slightly-mislabelled card than
        // to silently swallow a real S-rank.
        if (_config.ExpansionFilterEnabled)
        {
            var exp = FaloopData.ExpansionForTerritory(territoryId);
            if (exp.HasValue && !_config.ExpansionWhitelist.Contains((int)exp.Value))
                return;
        }

        // Resolve coordinates. A normal spawn has a precise "location" or a
        // single zonePoiId; SS "minion" reports carry several zonePoiIds at
        // once — collect every one so each gets a map marker AND its own
        // clickable chat flag (the old code kept only zonePoiIds[0]).
        var rawPts = new List<(int X, int Y)>();
        string? locationStr = GetString(spawnData, "location");
        int zonePoiId = 0;

        if (spawnData.TryGetProperty("zonePoiIds", out var pois) && pois.GetArrayLength() > 0)
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

        // A precise location supersedes the POI cloud (single refined point).
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

        float mapX = points.Count > 0 ? points[0].MapX : 0f;
        float mapY = points.Count > 0 ? points[0].MapY : 0f;
        int   rawX = points.Count > 0 ? points[0].RawX : 0;
        int   rawY = points.Count > 0 ? points[0].RawY : 0;

        var reporter = string.Empty;
        if (spawnData.TryGetProperty("reporters", out var reporters) && reporters.GetArrayLength() > 0)
            reporter = GetString(reporters[0], "name") ?? string.Empty;

        var zoneName = (territoryId > 0 ? LookupZoneName(territoryId) : null) ?? zoneSlug;
        // Lumina's BNpcName is sentence-cased (e.g. "the Pale Rider"). Normalise
        // to proper Title Case ("The Pale Rider") for clean card/chat output.
        var mobName  = ToTitleCase(LookupMobName(mobInfo.BNpcId) ?? mobSlug);

        // Use Faloop's reported timestamp (ISO 8601 in UTC) so "Age" reflects
        // actual time-since-spawn, not time-since-we-saw-the-event.
        var reportedAt = TryParseUtc(GetString(spawnData, "timestamp")) ?? DateTime.Now;

        var spawn = new SpawnInfo
        {
            World        = worldName,
            MobName      = mobName,
            ZoneName     = zoneName,
            X            = mapX,
            Y            = mapY,
            Rank         = rank,
            HpPercent    = 100,
            Reporter     = reporter,
            ReportedAt   = reportedAt,
            ZoneInstance = zoneInst,
            TerritoryId  = territoryId,
            MapId        = mapId,
            RawX         = rawX,
            RawY         = rawY,
            ZonePoiId    = zonePoiId,
            Points       = points,
            IsSS         = mobInfo.Rank == MobRank.SS,
            IsScheduled  = GetBool(spawnData, "isScheduled"),
            ScheduleDelay = spawnData.TryGetProperty("scheduleDelay", out var sd) &&
                            sd.ValueKind == JsonValueKind.Number &&
                            sd.TryGetInt32(out var sdv) ? sdv : null,
            // stage: int? — non-null while in pre-release window, null once
            // manual release fires. Renderer uses this to flip PRE-RELEASE→
            // RELEASED instantly, bypassing brittle ReportedAt+delay math.
            Stage         = spawnData.TryGetProperty("stage", out var st) &&
                            st.ValueKind == JsonValueKind.Number &&
                            st.TryGetInt32(out var stv) ? stv : null,
            RawEvent     = mobData.GetRawText(),
        };

        // S-2: Faloop legitimately re-emits `spawn` for the same mark, and a
        // reconnect can replay recent events. Without an identity key that
        // produced duplicate, un-killable cards (death/location only patch the
        // first match). Upsert on (mob, world, instance) while still alive, and
        // only fire the new-spawn alert for a genuinely new entry.
        bool isNew;
        lock (_lock)
        {
            var idx = _spawns.FindIndex(s =>
                !s.IsDead &&
                string.Equals(s.MobName, mobName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.World,   worldName, StringComparison.OrdinalIgnoreCase) &&
                s.ZoneInstance == zoneInst);

            if (idx >= 0)
            {
                // Watcher for the pre-release/early-access → public transition.
                // If the card we already had was scheduled (any sub-form:
                // timed pre-release, untimed pre-release, or permissioned
                // early-access via stage:null), and THIS event is no longer
                // scheduled, the mob just went genuinely public. Re-fire
                // OnNewSpawn so the user gets a fresh ding + echo at the
                // moment that actually matters for pulling — the previous
                // pre-release alert was just a heads-up.
                var prev = _spawns[idx];
                var wentPublic = prev.IsScheduled && !spawn.IsScheduled;
                spawn.JustWentPublic = wentPublic;
                // Stamp the moment of transition so the renderer can show a
                // visible JUST RELEASED badge (otherwise the badge silently
                // disappears and users can't tell if the card updated).
                // Preserve any existing stamp through later refreshes.
                spawn.PublicReleasedAt = wentPublic
                    ? TimeSync.ServerNow
                    : prev.PublicReleasedAt;
                _spawns[idx] = spawn;   // refresh in place
                isNew = wentPublic;

                // Debug breadcrumb: log every refresh on an existing card so
                // unexpected event shapes (action names, missing fields) are
                // visible in /xllog when something doesn't transition as
                // expected. Cheap — only fires on upserts, not new spawns.
                Plugin.Log.Debug(
                    $"[Faloop] Refresh {mobName}@{worldName} i{zoneInst}: " +
                    $"prev.scheduled={prev.IsScheduled} stage={prev.Stage?.ToString() ?? "null"} → " +
                    $"new.scheduled={spawn.IsScheduled} stage={spawn.Stage?.ToString() ?? "null"} " +
                    $"(wentPublic={wentPublic})");
            }
            else
            {
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

        SpawnInfo? target;
        uint       targetTerritory;
        lock (_lock)
        {
            target = _spawns.Find(s =>
                string.Equals(s.MobName, mobName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.World, worldName, StringComparison.OrdinalIgnoreCase) &&
                s.ZoneInstance == zoneInst);
            targetTerritory = target?.TerritoryId ?? 0;
        }
        if (target == null) return;

        var coords = ResolveCoords(targetTerritory, locationStr);
        if (coords == null) return;

        // M-1: mutate the shared SpawnInfo under the same lock that guards the
        // list — the render thread reads these fields off GetSnapshot()'s
        // shared references, so an unsynchronised write is a data race.
        lock (_lock)
        {
            target.X    = coords.Value.x;
            target.Y    = coords.Value.y;
            target.RawX = coords.Value.rawX;
            target.RawY = coords.Value.rawY;
            // A precise location collapses the POI cloud to one exact point.
            target.Points.Clear();
            target.Points.Add(new SpawnPoint(coords.Value.rawX, coords.Value.rawY,
                                             coords.Value.x, coords.Value.y));
            RebuildSnapshotLocked();
        }
        Plugin.Log.Debug($"[Faloop] Updated location for {mobName} on {worldName}: ({coords.Value.x:F1}, {coords.Value.y:F1})");

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

        lock (_lock)
        {
            var match = _spawns.Find(s =>
                string.Equals(s.MobName, mobName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.World, worldName, StringComparison.OrdinalIgnoreCase) &&
                s.ZoneInstance == zoneInst);
            if (match != null)
            {
                match.IsDead   = true;
                match.KilledAt = killedAt;
                RebuildSnapshotLocked();
            }
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
        DateTime? releaseTime = null;
        if (mobData.TryGetProperty("data", out var rData))
            releaseTime = TryParseUtc(GetString(rData, "timestamp"));

        SpawnInfo? released = null;
        lock (_lock)
        {
            var idx = _spawns.FindIndex(s =>
                !s.IsDead &&
                string.Equals(s.MobName, mobName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.World,   worldName, StringComparison.OrdinalIgnoreCase) &&
                s.ZoneInstance == zoneInst);

            if (idx < 0)
            {
                // No card to flip — release for a spawn we never saw the
                // pre-release of. Nothing useful to do here; ignore.
                Plugin.Log.Debug($"[Faloop] spawn_release for unknown {mobName}@{worldName} i{zoneInst}");
                return;
            }

            var prev = _spawns[idx];
            // Build the released record by cloning the previous one and
            // flipping the scheduled state. We preserve every other field
            // (points, reporter, route hint, …) — the only thing changing
            // is "is this visible to the public yet."
            released = new SpawnInfo
            {
                World            = prev.World,
                MobName          = prev.MobName,
                ZoneName         = prev.ZoneName,
                X                = prev.X,
                Y                = prev.Y,
                Rank             = prev.Rank,
                HpPercent        = prev.HpPercent,
                Reporter         = prev.Reporter,
                ReportedAt       = releaseTime ?? DateTime.Now,
                RawEvent         = mobData.GetRawText(),
                ZoneInstance     = prev.ZoneInstance,
                TerritoryId      = prev.TerritoryId,
                MapId            = prev.MapId,
                RawX             = prev.RawX,
                RawY             = prev.RawY,
                ZonePoiId        = prev.ZonePoiId,
                Points           = prev.Points,
                IsSS             = prev.IsSS,
                IsScheduled      = false,           // ← the flip
                ScheduleDelay    = null,
                Stage            = null,
                JustWentPublic   = true,
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
            var idx = _spawns.FindIndex(s =>
                !s.IsDead &&
                string.Equals(s.MobName, mobName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.World,   worldName, StringComparison.OrdinalIgnoreCase) &&
                s.ZoneInstance == zoneInst);

            if (idx < 0)
            {
                Plugin.Log.Debug($"[Faloop] spawn_progress for unknown {mobName}@{worldName} i{zoneInst} ph{phaseNum}");
                return;
            }

            var prev = _spawns[idx];

            // Filter the phase's POI list to those that resolve to coords in
            // our table — implicitly drops POIs from other zones since cross-
            // zone IDs won't be relevant (every POI lives in exactly one
            // zone, so unresolved-in-this-context = different zone).
            var narrowedRaw = new List<(int X, int Y)>();
            var narrowedPois = new List<int>();
            foreach (var poiId in phase.ZonePoiIds)
            {
                if (FaloopData.Locations.TryGetValue(poiId, out var ploc) &&
                    TryParseRaw(ploc, out var px, out var py))
                {
                    narrowedRaw.Add((px, py));
                    narrowedPois.Add(poiId);
                }
            }

            // Convert raw → map coords using the spawn's existing territory.
            var points = new List<SpawnPoint>();
            if (prev.TerritoryId > 0)
            {
                foreach (var rp in narrowedRaw)
                {
                    var c = ResolveCoords(prev.TerritoryId, $"{rp.X},{rp.Y}");
                    if (c.HasValue)
                        points.Add(new SpawnPoint(c.Value.rawX, c.Value.rawY,
                                                  c.Value.x, c.Value.y));
                }
            }

            // Filter further by spawn's current zone POIs. If the original
            // spawn had specific POI markers and at least some of the
            // narrowed points overlap with the spawn's territory, keep
            // them; otherwise we'd accidentally show another zone's set.
            // (gK does this via Zone.forId(zoneId).pois — we approximate
            // by demanding ResolveCoords succeeded against this territory.)
            if (points.Count == 0)
            {
                // Nothing resolved in this zone — likely a cross-zone POI
                // set we shouldn't apply to this spawn. Keep prev points.
                points = prev.Points;
            }

            next = new SpawnInfo
            {
                World            = prev.World,
                MobName          = prev.MobName,
                ZoneName         = prev.ZoneName,
                X                = points.Count > 0 ? points[0].MapX : prev.X,
                Y                = points.Count > 0 ? points[0].MapY : prev.Y,
                Rank             = prev.Rank,
                HpPercent        = prev.HpPercent,
                Reporter         = prev.Reporter,
                ReportedAt       = progressTime ?? prev.ReportedAt,
                RawEvent         = mobData.GetRawText(),
                ZoneInstance     = prev.ZoneInstance,
                TerritoryId      = prev.TerritoryId,
                MapId            = prev.MapId,
                RawX             = points.Count > 0 ? points[0].RawX : prev.RawX,
                RawY             = points.Count > 0 ? points[0].RawY : prev.RawY,
                ZonePoiId        = narrowedPois.Count > 0 ? narrowedPois[0] : prev.ZonePoiId,
                Points           = points,
                IsSS             = prev.IsSS,
                IsScheduled      = prev.IsScheduled,
                ScheduleDelay    = prev.ScheduleDelay,
                Stage            = phaseNum,
                JustWentPublic   = false,
                PublicReleasedAt = prev.PublicReleasedAt,
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
            _spawns.RemoveAll(s =>
                string.Equals(s.MobName, mobName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.World, worldName, StringComparison.OrdinalIgnoreCase) &&
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
        _cts.Cancel();
        // Give the loop a moment to unwind on unload; it exits promptly on
        // cancellation (ReceiveAsync throws, Task.Delay cancels).
        try { _loopTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* AggregateException(OperationCanceled) — expected */ }
        _cts.Dispose();
    }
}
