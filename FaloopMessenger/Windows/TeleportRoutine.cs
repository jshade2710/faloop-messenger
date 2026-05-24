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
    // Spawn keys currently mid-teleport → drives the TP-button loading state
    // for whichever window is rendering the card.
    internal static readonly HashSet<long> InProgress = new();

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
            var link = new MapLinkPayload(spawn.TerritoryId, spawn.MapId, spawn.X, spawn.Y);
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
        InProgress.Add(spawnKey);

        try
        {
            if (spawn.TerritoryId == 0)
            {
                SafePrint("[FaloopMessenger] No territory id for this spawn.");
                return;
            }

            string? aetheryteName = null;
            if (spawn.ZonePoiId > 0 &&
                FaloopRoutes.RouteByPoiId.TryGetValue(spawn.ZonePoiId, out var faloopRoute))
            {
                aetheryteName = faloopRoute.Aetheryte;
            }
            else
            {
                aetheryteName = await Plugin.Framework.RunOnFrameworkThread(() =>
                    FindAetheryteForTerritory(spawn.TerritoryId, spawn.RawX, spawn.RawY));
            }

            if (string.IsNullOrEmpty(aetheryteName))
            {
                SafePrint($"[FaloopMessenger] No aetheryte known for {spawn.ZoneName}.");
                return;
            }

            Plugin.Log.Information($"[Faloop] /li {spawn.World} → /li {aetheryteName}");

            await Plugin.Framework.RunOnFrameworkThread(() =>
                Plugin.CommandManager.ProcessCommand($"/li {spawn.World.ToLowerInvariant()}"));

            await WaitForLifestreamIdle();

            await Plugin.Framework.RunOnFrameworkThread(() =>
                Plugin.CommandManager.ProcessCommand($"/li {aetheryteName.ToLowerInvariant()}"));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Faloop] Teleport flow failed");
            SafePrint($"[FaloopMessenger] Teleport error: {ex.Message}");
        }
        finally
        {
            InProgress.Remove(spawnKey);
        }
    }

    // Wait until Lifestream's IsBusy IPC reports false. Falls back to a
    // TerritoryChanged + stability window if Lifestream isn't installed.
    private static async Task WaitForLifestreamIdle()
    {
        Dalamud.Plugin.Ipc.ICallGateSubscriber<bool>? isBusy = null;
        try { isBusy = Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy"); }
        catch { isBusy = null; }

        if (isBusy != null)
        {
            await Task.Delay(750);
            var deadline = DateTime.Now.AddSeconds(120);
            while (DateTime.Now < deadline)
            {
                bool busy;
                try { busy = await Plugin.Framework.RunOnFrameworkThread(() => isBusy.InvokeFunc()); }
                catch { busy = false; }

                if (!busy)
                {
                    await Task.Delay(250);
                    return;
                }
                await Task.Delay(500);
            }
            Plugin.Log.Warning("[Faloop] Lifestream still busy after 2 min; proceeding anyway.");
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
