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

    // Build a chat-train-style spawn message in the form
    //
    //   [HunterMarkS] Marilith [CrossWorld] Siren [Instance2] <flag>
    //
    // The flag autotranslate references the user's currently-planted flag —
    // so we plant it at the spawn coords first to make sure clicking it
    // teleports the recipient to the right spot, not wherever the user
    // last had a flag. The message is printed to local /echo as a preview
    // AND copied to clipboard (icons stripped, "<flag>" preserved literally
    // since FFXIV's chat input auto-expands the typed token) so the user
    // can paste straight into FC chat or a linkshell.
    //
    // Future v0.5+: this will become the relay entry-point that sends to
    // user-checked cross-world linkshells / linkshells via chat-input
    // automation (see roadmap).
    public static void Ping(SpawnInfo spawn)
    {
        try
        {
            // Plant the flag first so <flag> in the message resolves to the
            // spawn point (multi-POI SS spawns use the first reported point).
            if (spawn.TerritoryId > 0 && spawn.MapId > 0 &&
                (spawn.X > 0 && spawn.Y > 0))
            {
                var link = new Dalamud.Game.Text.SeStringHandling.Payloads.MapLinkPayload(
                    spawn.TerritoryId, spawn.MapId, spawn.X, spawn.Y);
                Plugin.GameGui.OpenMapWithMapLink(link);
            }

            // Build the rich SeString for local echo (full icons).
            var rank   = spawn.IsSS ? "SS" : spawn.Rank.ToString();
            var sb     = new Dalamud.Game.Text.SeStringHandling.SeStringBuilder();

            // Hunt rank icon — falls back to bracketed text on enum mismatch.
            TryAddRankIcon(sb, spawn.Rank, spawn.IsSS, $"[{rank}] ");

            sb.AddText(spawn.MobName).AddText(" ");

            // World decoration icon (the ❀ that prefixes a visiting player's
            // world name on the FC roster / chat). Falls back to "on " if
            // the enum value isn't available.
            TryAddIcon(sb, "CrossWorld", " on ");
            sb.AddText(spawn.World);

            // Instance indicator on the same line.
            if (spawn.ZoneInstance > 0)
            {
                sb.AddText(" ");
                TryAddInstanceIcon(sb, spawn.ZoneInstance, $"i{spawn.ZoneInstance}");
            }

            sb.AddText(" ");
            // <flag> autotranslate. The (group, key) is FFXIV's
            // autotranslate table for the Sort-Map-Flag phrase. The
            // safest fallback is the literal "<flag>" string, which the
            // game's chat input also auto-expands when typed.
            TryAddFlagAutoTranslate(sb, "<flag>");

            // Local echo preview.
            Plugin.ChatGui.Print(sb.Build());

            // Clipboard copy — plain text only (icons can't survive
            // clipboard round-trip), with "<flag>" preserved so the
            // chat input expands it on paste.
            var inst = spawn.ZoneInstance > 0 ? $" i{spawn.ZoneInstance}" : string.Empty;
            var plain = $"{rank} {spawn.MobName} {spawn.World}{inst} <flag>";
            try { ImGui.SetClipboardText(plain); }
            catch (Exception ex) { Plugin.Log.Warning($"[Faloop] Ping clipboard copy failed: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[Faloop] Ping failed: {ex.Message}");
        }
    }

    // ── Icon-payload helpers ──────────────────────────────────────────
    //
    // BitmapFontIcon's enum members vary slightly between Dalamud versions
    // (HunterMarkS vs MarkS, CrossWorld vs WorldTraveling, etc.). Rather
    // than hard-pin a name and risk a runtime mismatch, we resolve by
    // string and gracefully fall back to plain text. The icons themselves
    // are non-essential — message readability survives without them.

    private static void TryAddRankIcon(
        Dalamud.Game.Text.SeStringHandling.SeStringBuilder sb,
        HuntRank rank, bool isSS, string fallback)
    {
        // Try a few likely enum names in priority order. SS shares the S
        // icon on FFXIV's hunt board (no distinct SS pictograph).
        var names = (rank, isSS) switch
        {
            (_, true)       => new[] { "HuntingLogRefresh", "HunterMarkS", "MarkS" },
            (HuntRank.S, _) => new[] { "HunterMarkS", "MarkS" },
            (HuntRank.A, _) => new[] { "HunterMarkA", "MarkA" },
            (HuntRank.B, _) => new[] { "HunterMarkB", "MarkB" },
            _               => Array.Empty<string>(),
        };
        foreach (var n in names) if (TryAddIcon(sb, n, null)) return;
        sb.AddText(fallback);
    }

    private static void TryAddInstanceIcon(
        Dalamud.Game.Text.SeStringHandling.SeStringBuilder sb,
        int inst, string fallback)
    {
        if (inst >= 1 && inst <= 9)
        {
            if (TryAddIcon(sb, $"Instance{inst}", null)) return;
        }
        sb.AddText(fallback);
    }

    // Returns true if an icon payload was successfully appended.
    private static bool TryAddIcon(
        Dalamud.Game.Text.SeStringHandling.SeStringBuilder sb,
        string enumName, string? fallback)
    {
        try
        {
            var iconType  = typeof(Dalamud.Game.Text.SeStringHandling.Payloads.IconPayload).Assembly
                .GetType("Dalamud.Game.Text.SeStringHandling.BitmapFontIcon");
            if (iconType == null)
            {
                if (fallback != null) sb.AddText(fallback);
                return false;
            }
            if (!Enum.TryParse(iconType, enumName, out var val) || val == null)
            {
                if (fallback != null) sb.AddText(fallback);
                return false;
            }
            var payload = (Dalamud.Game.Text.SeStringHandling.Payloads.IconPayload)
                Activator.CreateInstance(typeof(Dalamud.Game.Text.SeStringHandling.Payloads.IconPayload),
                    val)!;
            sb.Add(payload);
            return true;
        }
        catch
        {
            if (fallback != null) sb.AddText(fallback);
            return false;
        }
    }

    private static void TryAddFlagAutoTranslate(
        Dalamud.Game.Text.SeStringHandling.SeStringBuilder sb,
        string fallback)
    {
        // The "<flag>" autotranslate in FFXIV's chat input is the Sort/
        // Map/Flag phrase. Its (sheet, row) varies by patch but Dalamud
        // exposes AutoTranslatePayload(uint group, uint key). We try
        // the well-known classic value (group=33, key=89 — historically
        // the flag autotranslate) and fall back to literal text on miss.
        try
        {
            var payload = new Dalamud.Game.Text.SeStringHandling.Payloads.AutoTranslatePayload(33, 89);
            sb.Add(payload);
        }
        catch
        {
            sb.AddText(fallback);
        }
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
