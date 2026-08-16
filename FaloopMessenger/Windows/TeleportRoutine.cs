using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace FaloopMessenger.Windows;

// All "act on a spawn" verbs — flag, ping, party, teleport — plus the
// closest-aetheryte lookup and the startup audit. Kept static so any window
// (Main / Mini / Compact) can call them uniformly.
internal static class TeleportRoutine
{
    // M-6 fix (v0.4.7 audit): InProgress is touched from the render thread
    // (button click → Add, Contains) AND from Teleport()'s async continuation
    // (Remove in finally). HashSet<T> isn't thread-safe; ConcurrentDictionary
    // is, and a key-only set falls out of `ConcurrentDictionary<long, byte>`.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _inProgress = new();

    /// <summary>Whether a spawn is currently mid-teleport (drives the "TP'ing…"
    /// button state for whichever window is rendering the card).</summary>
    internal static bool IsInProgress(long spawnKey) => _inProgress.ContainsKey(spawnKey);

    /// <summary>Remove a spawn's in-progress marker. Idempotent; safe across
    /// threads. Used by the renderer's dismiss-spawn path.</summary>
    internal static void ClearInProgress(long spawnKey) => _inProgress.TryRemove(spawnKey, out _);

    // ── Lifestream IPC (v0.4.16) ──────────────────────────────────────
    //
    // Lifestream 2.5.x exposes a typed ECommons EzIPC surface with real
    // return values. We previously drove it by firing blind chat commands
    // (CommandManager.ProcessCommand("/li …")), which return NOTHING — so
    // when a teleport half-worked the plugin had no idea which step failed
    // and every diagnosis was guesswork. These subscribers give us a
    // success/failure signal per step.
    //
    //   bool IsBusy()                     TaskManager + FollowPath status
    //   bool ChangeWorld(string world)    same-DC or cross-DC world visit
    //   bool CanVisitSameDC(string world) pre-flight validation
    //   bool CanVisitCrossDC(string world)
    //   void ExecuteCommand(string args)  runs a "/li …" through Lifestream
    //
    // Subscribers are created lazily and cached; creation never throws
    // (Dalamud builds the gate on demand) — only invocation does, when the
    // provider side isn't registered.
    private static Dalamud.Plugin.Ipc.ICallGateSubscriber<bool>?           _lsIsBusy;
    private static Dalamud.Plugin.Ipc.ICallGateSubscriber<string, bool>?   _lsChangeWorld;
    private static Dalamud.Plugin.Ipc.ICallGateSubscriber<string, bool>?   _lsCanVisitSameDc;
    private static Dalamud.Plugin.Ipc.ICallGateSubscriber<string, bool>?   _lsCanVisitCrossDc;
    private static Dalamud.Plugin.Ipc.ICallGateSubscriber<string, object>? _lsExecuteCommand;

    private static void EnsureLifestreamGates()
    {
        _lsIsBusy          ??= Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        _lsChangeWorld     ??= Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        _lsCanVisitSameDc  ??= Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitSameDC");
        _lsCanVisitCrossDc ??= Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.CanVisitCrossDC");
        _lsExecuteCommand  ??= Plugin.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");
    }

    // Invoke a bool-returning Lifestream IPC on the framework thread.
    // Returns `fallback` (and logs) if the provider isn't registered.
    private static async Task<bool> LsBoolAsync(
        Dalamud.Plugin.Ipc.ICallGateSubscriber<string, bool>? gate,
        string arg, string label, bool fallback)
    {
        if (gate == null) return fallback;
        try
        {
            return await Plugin.Framework.RunOnFrameworkThread(() => gate.InvokeFunc(arg));
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Faloop] Lifestream.{label}(\"{arg}\") unavailable: {ex.Message}");
            return fallback;
        }
    }

    // M-3 fix (v0.4.7 audit): probe Lifestream's IPC at startup so the TP
    // button can disable itself with an actionable tooltip when the user
    // doesn't have Lifestream installed.
    //
    // Caching policy (B-1 from v0.4.7 self-review): only cache POSITIVE
    // results. A user who installs Lifestream mid-session would otherwise
    // see TP disabled forever — we'd never re-check. Negative results get
    // re-probed at most every 30 seconds (cheap — one GetIpcSubscriber +
    // InvokeFunc call) so the cost is bounded but the answer can change.
    private static bool _lifestreamKnownGood;
    private static long _lastLifestreamProbeTicks;
    private const   long LifestreamProbeMinIntervalMs = 30_000;

    internal static bool LifestreamAvailable
    {
        get
        {
            if (_lifestreamKnownGood) return true;
            var now = Environment.TickCount64;
            if (now - System.Threading.Interlocked.Read(ref _lastLifestreamProbeTicks)
                < LifestreamProbeMinIntervalMs)
                return false;   // recently probed and missing — don't hammer the IPC
            System.Threading.Interlocked.Exchange(ref _lastLifestreamProbeTicks, now);

            try
            {
                EnsureLifestreamGates();
                _lsIsBusy!.InvokeFunc();   // throws if the provider isn't registered
                _lifestreamKnownGood = true;
                return true;
            }
            catch { return false; }
        }
    }

    // Manual aetheryte overrides for zones whose nearest-useful aetheryte
    // lives in a different territory in FFXIV's data (city aetherytes that
    // happen to border the hunting zone, etc.).
    private static readonly Dictionary<uint, string> AetheryteOverrides = new()
    {
        { 399, "Idyllshire" },          // The Dravanian Hinterlands → Idyllshire (separate territory in Lumina)
        // Add more here as the startup audit reports them.
    };

    // Chat output from Teleport()'s async continuations would otherwise run on
    // a thread-pool thread (the await resumes off the framework thread). Calling
    // ChatGui from a background thread can corrupt game memory and crash FFXIV,
    // so every user-facing message is marshaled back onto the framework thread.
    private static void SafePrint(string message)
    {
        try
        {
            Plugin.Framework.RunOnFrameworkThread(() =>
            {
                try { Plugin.ChatGui.Print(message); }
                catch (Exception ex) { Plugin.Log.Warning($"[Faloop] Print failed: {ex.Message}"); }
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Faloop] SafePrint marshal failed: {ex.Message}");
        }
    }

    // ── Public verbs ──────────────────────────────────────────────────

    public static void SetFlag(SpawnInfo spawn)
    {
        try
        {
            // Read coords from Points[0] — the SAME authoritative source the
            // chat echo (Plugin.PrintSpawnEcho) and the card renderer
            // (SpawnCardRenderer.DrawMapThumb) use. spawn.X / spawn.Y are a
            // denormalised mirror of Points[0] that can drift out of sync
            // across the coordless→location→release upsert sequence (a spawn
            // that arrived without coords, then got them, could end up with
            // Points populated but X/Y stale at 0 — planting the flag at the
            // map origin, ~0.1,0.1). Points is the single source of truth;
            // fall back to X/Y only when Points is somehow empty.
            float mapX, mapY;
            if (spawn.Points.Count > 0)
            {
                mapX = spawn.Points[0].MapX;
                mapY = spawn.Points[0].MapY;
            }
            else
            {
                mapX = spawn.X;
                mapY = spawn.Y;
            }

            if (spawn.TerritoryId == 0 || spawn.MapId == 0 || (mapX <= 0 && mapY <= 0))
            {
                Plugin.Log.Warning(
                    $"[Faloop] SetFlag skipped for {spawn.MobName}@{spawn.World}: " +
                    $"no resolvable location (territory={spawn.TerritoryId} map={spawn.MapId} " +
                    $"pts={spawn.Points.Count} x={mapX:F1} y={mapY:F1}).");
                return;
            }

            var link = new MapLinkPayload(spawn.TerritoryId, spawn.MapId, mapX, mapY);
            Plugin.GameGui.OpenMapWithMapLink(link);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Faloop] SetFlag failed: {ex.Message}");
        }
    }

    // Prints a clickable map link to local Echo chat. Does NOT auto-open the
    // map / plant the flag — that's a separate explicit action (click the
    // map thumbnail on the card).
    public static void Ping(SpawnInfo spawn)
    {
        Plugin.PrintSpawnEcho(spawn);
    }

    // Open Party Finder pre-filled for a hunt: Hunt category, "S Rank"
    // description, and "limit recruiting to current world" enabled (the usual
    // hunt-train default so off-world randoms don't fill slots).
    public static void OpenPartyFinder()
    {
        try
        {
            unsafe
            {
                var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentLookingForGroup.Instance();
                if (agent == null) return;

                var info = &agent->StoredRecruitmentInfo;

                // Typed fields — safe (FFXIVClientStructs keeps these accurate
                // across game patches; no raw offset math).
                info->SelectedCategory =
                    FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentLookingForGroup.DutyCategory.TheHunt;
                info->LimitRecruitingToWorld = 0;   // 0 = limited to world, 1 = cross-world

                // Typed comment setter — FFXIVClientStructs owns the buffer and
                // its bounds. No hardcoded offset / manual byte math, so a
                // struct-layout change in a game patch can't turn this into an
                // out-of-bounds write (which would be an uncatchable AV / hard
                // crash, not a .NET exception the try/catch could absorb).
                info->CommentString = "S Rank";

                agent->Show();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Faloop] Party Finder open failed: {ex.Message}");
        }
    }

    // Cross-world teleport via Lifestream — switches worlds then aetherytes.
    // Silent on success; chat output only on error. The TP button's "TP'ing…"
    // state and the route hint already on the card provide all the feedback
    // a user needs.
    public static async void Teleport(SpawnInfo spawn)
    {
        var spawnKey = spawn.ReportedAt.Ticks;
        _inProgress.TryAdd(spawnKey, 0);

        try
        {
            // M-3 fix: bail out loudly when Lifestream isn't installed. The
            // /li commands would silently no-op otherwise, leaving the user
            // wondering why nothing happened.
            if (!LifestreamAvailable)
            {
                SafePrint("[FaloopMessenger] Teleport requires the Lifestream plugin. " +
                          "Install it from the Dalamud plugin browser, then try again.");
                return;
            }

            if (spawn.TerritoryId == 0)
            {
                SafePrint("[FaloopMessenger] No territory id for this spawn.");
                return;
            }

            // Prefer Points[0]'s raw coords (authoritative, same as the flag
            // and echo) over the denormalised spawn.RawX/RawY mirror, which
            // can drift stale across the coordless→location→release sequence.
            var rawX = spawn.Points.Count > 0 ? spawn.Points[0].RawX : spawn.RawX;
            var rawY = spawn.Points.Count > 0 ? spawn.Points[0].RawY : spawn.RawY;

            string? aetheryteName = null;
            if (spawn.ZonePoiId > 0 &&
                FaloopRoutes.RouteByPoiId.TryGetValue(spawn.ZonePoiId, out var faloopRoute))
            {
                aetheryteName = faloopRoute.Aetheryte;
            }
            else
            {
                aetheryteName = await Plugin.Framework.RunOnFrameworkThread(() =>
                    FindAetheryteForTerritory(spawn.TerritoryId, rawX, rawY));
            }

            if (string.IsNullOrEmpty(aetheryteName))
            {
                SafePrint($"[FaloopMessenger] No aetheryte known for {spawn.ZoneName}.");
                return;
            }

            EnsureLifestreamGates();

            // Are we already on the target world? If so the whole world-visit
            // step (and its timing race) is skipped — a large fraction of
            // hunts are on your home world.
            var currentWorld = await Plugin.Framework.RunOnFrameworkThread(() =>
                Plugin.ObjectTable.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ToString() ?? string.Empty);
            var needWorldHop = !string.Equals(currentWorld, spawn.World, StringComparison.OrdinalIgnoreCase);

            Plugin.Log.Information(
                $"[Faloop] Teleport plan: world={spawn.World} (currently '{currentWorld}', " +
                $"hop={(needWorldHop ? "yes" : "skip")}) → aetheryte={aetheryteName}");

            if (needWorldHop)
            {
                // Pre-flight: Lifestream tells us up front whether this world
                // is reachable at all, so an impossible hop reports a clear
                // reason instead of failing silently mid-flow. Both checks
                // default to `true` when the IPC is missing so an older
                // Lifestream can't block us.
                var sameDc  = await LsBoolAsync(_lsCanVisitSameDc,  spawn.World, "CanVisitSameDC",  true);
                var crossDc = await LsBoolAsync(_lsCanVisitCrossDc, spawn.World, "CanVisitCrossDC", true);
                if (!sameDc && !crossDc)
                {
                    SafePrint($"[FaloopMessenger] Lifestream says {spawn.World} isn't visitable right now " +
                              "(travel restriction, congestion, or you're mid-duty).");
                    return;
                }

                // Typed IPC instead of a blind "/li <world>" chat command —
                // this one actually reports whether it took the job.
                var accepted = await LsBoolAsync(_lsChangeWorld, spawn.World, "ChangeWorld", fallback: false);
                if (!accepted)
                {
                    // Fall back to the legacy chat command for older
                    // Lifestream builds that lack ChangeWorld.
                    Plugin.Log.Warning("[Faloop] Lifestream.ChangeWorld declined/unavailable — " +
                                       "falling back to the /li chat command.");
                    await Plugin.Framework.RunOnFrameworkThread(() =>
                        Plugin.CommandManager.ProcessCommand($"/li {spawn.World.ToLowerInvariant()}"));
                }
                else
                {
                    Plugin.Log.Debug($"[Faloop] Lifestream.ChangeWorld(\"{spawn.World}\") accepted.");
                }

                // Block until Lifestream has actually finished the hop.
                await WaitForLifestreamIdle();
            }

            // Aetheryte step. Routed through Lifestream.ExecuteCommand rather
            // than the global chat handler so it can't be intercepted or
            // reordered by another plugin's /li handler.
            var aetheryteCmd = $"/li {aetheryteName.ToLowerInvariant()}";
            var viaIpc = false;
            if (_lsExecuteCommand != null)
            {
                try
                {
                    await Plugin.Framework.RunOnFrameworkThread(() =>
                        _lsExecuteCommand.InvokeAction(aetheryteName.ToLowerInvariant()));
                    viaIpc = true;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning($"[Faloop] Lifestream.ExecuteCommand unavailable: {ex.Message}");
                }
            }
            if (!viaIpc)
            {
                await Plugin.Framework.RunOnFrameworkThread(() =>
                    Plugin.CommandManager.ProcessCommand(aetheryteCmd));
            }
            Plugin.Log.Information(
                $"[Faloop] Aetheryte step issued ({(viaIpc ? "IPC" : "chat command")}): {aetheryteCmd}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Faloop] Teleport flow failed");
            SafePrint($"[FaloopMessenger] Teleport error: {ex.Message}");
        }
        finally
        {
            _inProgress.TryRemove(spawnKey, out _);
        }
    }

    // How long to give Lifestream to PICK UP the world command before we
    // conclude it isn't going to (see the two-phase wait below).
    private const int LifestreamStartupGraceSec = 15;
    // Hard ceiling on a world visit once Lifestream has actually started it.
    private const int LifestreamBusyTimeoutSec  = 120;
    // Breathing room after IsBusy goes false. A world transfer reports done
    // the instant the zone-in completes, but the client will drop a second
    // /li issued in that same moment.
    private const int LifestreamSettleMs        = 1200;

    // Wait for Lifestream to finish the world visit before we issue the
    // aetheryte command.
    //
    // TWO-PHASE, and the phases matter. The old single-phase version just
    // polled "is IsBusy false?" after a 750 ms head start — but IsBusy is
    // ALSO false during the window between us issuing /li <world> and
    // Lifestream actually picking it up (it has to plan a route, possibly
    // walk to an aethernet shard, open the world-visit menu). So "hasn't
    // started yet" was indistinguishable from "already finished": we'd
    // return after ~1 s and fire /li <aetheryte> into a Lifestream that was
    // mid-transfer, which silently drops it. Symptom: the world hop works,
    // the aetheryte teleport never happens. It was always a race; anything
    // that lengthened Lifestream's startup (a Lifestream/Dalamud update, a
    // longer walk to a shard) flipped it from usually-winning to usually-
    // losing.
    //
    // Phase 1 waits for IsBusy to go TRUE  — proof Lifestream took the job.
    // Phase 2 waits for IsBusy to go FALSE — proof the job is done.
    private static async Task WaitForLifestreamIdle()
    {
        EnsureLifestreamGates();
        var isBusy = _lsIsBusy;

        if (isBusy != null)
        {
            // IPC errors read as "not busy" — same as the old behaviour, and
            // the bounded phases below keep a broken IPC from hanging us.
            async Task<bool> PollBusyAsync()
            {
                try { return await Plugin.Framework.RunOnFrameworkThread(() => isBusy.InvokeFunc()); }
                catch { return false; }
            }

            // ── Phase 1: wait for Lifestream to pick the command up ──
            var sawBusy       = false;
            var startDeadline = DateTime.Now.AddSeconds(LifestreamStartupGraceSec);
            while (DateTime.Now < startDeadline)
            {
                if (await PollBusyAsync()) { sawBusy = true; break; }
                await Task.Delay(250);
            }

            if (!sawBusy)
            {
                // Either the hop completed inside a poll gap (we were already
                // on the target world) or Lifestream ignored the command.
                // Settle briefly and let the caller try the aetheryte anyway —
                // a wrong-but-attempted teleport beats a silent no-op.
                Plugin.Log.Warning(
                    $"[Faloop] Lifestream never reported busy within {LifestreamStartupGraceSec}s " +
                    "of the world command — proceeding to the aetheryte step anyway.");
                await Task.Delay(LifestreamSettleMs);
                return;
            }

            // ── Phase 2: wait for it to finish ──
            var doneDeadline = DateTime.Now.AddSeconds(LifestreamBusyTimeoutSec);
            while (DateTime.Now < doneDeadline)
            {
                if (!await PollBusyAsync())
                {
                    await Task.Delay(LifestreamSettleMs);
                    return;
                }
                await Task.Delay(500);
            }
            Plugin.Log.Warning(
                $"[Faloop] Lifestream still busy after {LifestreamBusyTimeoutSec}s; proceeding anyway.");
            return;
        }

        // Fallback: territory-change + stability window
        var changedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastChange = DateTime.MinValue;
        void OnTerritoryChanged(uint _)
        {
            lastChange = DateTime.Now;
            changedTcs.TrySetResult(true);
        }
        Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
        try
        {
            var winner = await Task.WhenAny(changedTcs.Task, Task.Delay(10_000));
            if (winner == changedTcs.Task)
            {
                var deadline = DateTime.Now.AddSeconds(30);
                while (DateTime.Now < deadline &&
                       DateTime.Now - lastChange < TimeSpan.FromSeconds(6))
                    await Task.Delay(500);
            }
        }
        finally
        {
            Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
        }
    }

    // ── Aetheryte lookup ──────────────────────────────────────────────

    public static string? FindAetheryteForTerritory(uint territoryId, int rawX, int rawY)
    {
        if (AetheryteOverrides.TryGetValue(territoryId, out var manual))
            return manual;

        var slug = FaloopData.SlugForTerritory(territoryId);
        if (slug == null) return null;

        if (!FaloopData.ZoneAetherytes.TryGetValue(slug, out var aetherytes) ||
            aetherytes.Length == 0)
            return null;

        if (aetherytes.Length == 1) return aetherytes[0].Name;
        if (rawX <= 0 && rawY <= 0) return aetherytes[0].Name;

        string? bestName = null;
        var     bestSq   = double.MaxValue;
        foreach (var (name, ax, ay) in aetherytes)
        {
            double dx = ax - rawX;
            double dy = ay - rawY;
            var d = dx * dx + dy * dy;
            if (d < bestSq)
            {
                bestSq   = d;
                bestName = name;
            }
        }
        return bestName;
    }

    // ── Diagnostics ───────────────────────────────────────────────────

    // One-shot audit: which Faloop hunt territories can resolve to an aetheryte
    // (either via the override table, Faloop's ZoneAetherytes, or both). Logs
    // anything missing so we can add to AetheryteOverrides.
    public static void AuditAetherytes()
    {
        var missing = new List<(string slug, uint terrId)>();
        var found   = 0;

        foreach (var kvp in FaloopData.TerritoryTypes)
        {
            var name = FindAetheryteForTerritory(kvp.Value, 0, 0);
            if (string.IsNullOrEmpty(name)) missing.Add((kvp.Key, kvp.Value));
            else                            found++;
        }

        if (missing.Count == 0)
        {
            Plugin.Log.Information($"[Faloop] Aetheryte audit OK: {found}/{FaloopData.TerritoryTypes.Count} zones have aetherytes.");
            return;
        }

        Plugin.Log.Warning($"[Faloop] Aetheryte audit: {found} OK, {missing.Count} missing:");
        foreach (var (slug, tid) in missing)
            Plugin.Log.Warning($"[Faloop]   {slug} (territory {tid}) has no aetheryte");
    }
}
