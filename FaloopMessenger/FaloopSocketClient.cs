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
    private          CancellationTokenSource _cts = new();
    private          string? _sessionId;

    public FaloopSocketClient(Configuration config) => _config = config;

    // ── Public API ────────────────────────────────────────────────────

    public SpawnInfo[] GetSnapshot()
    {
        lock (_lock) return _spawns.ToArray();
    }

    // Manually remove a spawn from the tracker (e.g. user dismissing a stale entry).
    public void RemoveSpawn(SpawnInfo spawn)
    {
        bool removed;
        lock (_lock) removed = _spawns.Remove(spawn);
        if (removed) OnUpdate?.Invoke();
    }

    public void Connect()
    {
        if (State is ConnectionState.Connecting or ConnectionState.Connected) return;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _ = ConnectLoop(_cts.Token);
    }

    public void Disconnect()
    {
        _cts.Cancel();
        SetState(ConnectionState.Disconnected);
    }

    public void Reconnect()
    {
        Disconnect();
        _cts = new CancellationTokenSource();
        _ = ConnectLoop(_cts.Token);
    }

    // ── Connection loop ───────────────────────────────────────────────

    private async Task ConnectLoop(CancellationToken ct)
    {
        // Exponential backoff: 5s → 10s → 30s → 60s → 120s cap. Resets to 5s
        // after every successful connection.
        var consecutiveFailures = 0;

        // Periodic time-sync background task (started after first connect)
        Task? timeSyncTask = null;
        var timeSyncCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        while (!ct.IsCancellationRequested)
        {
            SetState(ConnectionState.Connecting);
            var connectedOnce = false;
            try
            {
                _sessionId = await FetchAnonSession(ct);

                var wsUrl = _config.SocketUrl;
                Plugin.Log.Information($"[Faloop] Connecting to {wsUrl}");

                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Origin",     FaloopOrigin);
                ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 Edg/122.0.0.0");

                await ws.ConnectAsync(new Uri(wsUrl), ct);

                // Successful WebSocket open — reset backoff and start the
                // periodic time-sync background task if not already running.
                consecutiveFailures = 0;
                connectedOnce       = true;
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

            if (!connectedOnce) consecutiveFailures++;
            var delaySec = Math.Min(120, 5 * (int)Math.Pow(2, Math.Min(consecutiveFailures, 5)));
            if (consecutiveFailures > 1)
                Plugin.Log.Information($"[Faloop] Reconnect attempt #{consecutiveFailures + 1} in {delaySec}s");
            await Task.Delay(TimeSpan.FromSeconds(delaySec), ct).ConfigureAwait(false);
        }

        timeSyncCts.Cancel();
        SetState(ConnectionState.Disconnected);
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
            if (string.IsNullOrWhiteSpace(_config.Username) || string.IsNullOrWhiteSpace(_config.Password))
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
                username   = _config.Username,
                password   = _config.Password,
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
        var sb  = new StringBuilder(512);

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            sb.Clear();
            WebSocketReceiveResult result;

            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    SetState(ConnectionState.Disconnected);
                    return;
                }

                sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
            }
            while (!result.EndOfMessage);

            await HandlePacket(sb.ToString(), ws, ct);
        }
    }

    // ── Protocol handling ─────────────────────────────────────────────

    private async Task HandlePacket(string packet, ClientWebSocket ws, CancellationToken ct)
    {
        if (packet.Length == 0) return;

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

            Plugin.Log.Debug($"[Faloop] message action={action} mob={mobSlug} world={worldSlug}");

            switch (action)
            {
                case "spawn":
                    HandleSpawnAction(mobSlug, worldSlug, zoneInst, mobData);
                    break;
                case "spawn_location":
                    HandleSpawnLocationAction(mobSlug, worldSlug, zoneInst, mobData);
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

        // Resolve coordinates — direct "location" field first, then POI lookup
        string? locationStr = GetString(spawnData, "location");
        int zonePoiId = 0;
        if (spawnData.TryGetProperty("zonePoiIds", out var pois) && pois.GetArrayLength() > 0)
        {
            zonePoiId = pois[0].GetInt32();
            if (locationStr == null)
                FaloopData.Locations.TryGetValue(zonePoiId, out locationStr);
        }

        float mapX = 0f, mapY = 0f;
        int   rawX = 0, rawY = 0;
        uint  mapId = 0;

        if (territoryId > 0)
        {
            var sheet = Plugin.DataManager.GetExcelSheet<TerritoryType>();
            if (sheet != null && sheet.TryGetRow(territoryId, out var tt))
            {
                mapId = tt.Map.ValueNullable?.RowId ?? 0;
                var coords = ResolveCoords(territoryId, locationStr ?? string.Empty);
                if (coords.HasValue)
                {
                    mapX = coords.Value.x;
                    mapY = coords.Value.y;
                    rawX = coords.Value.rawX;
                    rawY = coords.Value.rawY;
                }
            }
        }

        var reporter = string.Empty;
        if (spawnData.TryGetProperty("reporters", out var reporters) && reporters.GetArrayLength() > 0)
            reporter = GetString(reporters[0], "name") ?? string.Empty;

        var zoneName = (territoryId > 0 ? LookupZoneName(territoryId) : null) ?? zoneSlug;
        // Lumina's BNpcName is sentence-cased (e.g. "the Pale Rider"). Normalise
        // to lowercase so the card / chat output is visually consistent.
        var mobName  = (LookupMobName(mobInfo.BNpcId) ?? mobSlug).ToLowerInvariant();

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
            RawEvent     = mobData.GetRawText(),
        };

        lock (_lock)
        {
            _spawns.Insert(0, spawn);
            while (_spawns.Count > _config.MaxEntries)
                _spawns.RemoveAt(_spawns.Count - 1);
        }

        OnNewSpawn?.Invoke(spawn);
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
        lock (_lock)
        {
            target = _spawns.Find(s =>
                string.Equals(s.MobName, mobName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.World, worldName, StringComparison.OrdinalIgnoreCase) &&
                s.ZoneInstance == zoneInst);
        }
        if (target == null) return;

        var coords = ResolveCoords(target.TerritoryId, locationStr);
        if (coords == null) return;

        target.X    = coords.Value.x;
        target.Y    = coords.Value.y;
        target.RawX = coords.Value.rawX;
        target.RawY = coords.Value.rawY;
        Plugin.Log.Debug($"[Faloop] Updated location for {mobName} on {worldName}: ({target.X:F1}, {target.Y:F1})");

        OnUpdate?.Invoke();
    }

    // Convert Faloop's raw 2048-scale "x,y" string to in-game map coords using
    // the territory's map SizeFactor. Returns null if anything fails.
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

    private static string? GetString(JsonElement e, string key) =>
        e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

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
        _cts.Dispose();
    }
}
