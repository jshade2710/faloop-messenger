using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace FaloopMessenger.Windows;

// Ultra-compact tracker. One row per live spawn in a fixed-height scrollable
// table — `mob · world`, age, and three inline action buttons (TP / Flag /
// PF). No card chrome, no map thumbnail, no meta-row. Designed for the
// hunter who wants a passive "what's live right now" stripe on screen
// while doing something else, opened via /faloopmicro.
//
// Visual style intentionally minimal: standard ImGui widgets rather than
// the custom-drawn buttons in SpawnCardRenderer, because the row is too
// tight for the gold-accent treatment to read well, and the standard
// widgets stay legible at any UI scale without their own font handling.
public class MicroWindow : Window, IDisposable
{
    private readonly Plugin _plugin;

    public MicroWindow(Plugin plugin)
        : base("Faloop · Micro##faloopmicro",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        // Height is computed every frame from the spawn count (1 row when
        // there's a single spawn, 2 rows when there are 2+, scroll past
        // that). AlwaysAutoResize lets the window shrink-wrap to whatever
        // the table reports — no min/max height needed. Width is still
        // user-resizable via the constraint range below; the FirstUseEver
        // Size gives an initial value but doesn't pin it.
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420f, 0f),
            MaximumSize = new Vector2(900f, float.MaxValue),
        };
        SizeCondition = ImGuiCond.FirstUseEver;
        Size          = new Vector2(540f, 0f);
        _plugin       = plugin;
    }

    public void Dispose() { }

    public override bool DrawConditions() => !Plugin.HiddenInInstance(_plugin.Configuration);

    public override void Draw()
    {
        var snapshot = _plugin.Client.GetSnapshot();

        // Count live spawns once — drives both the empty-state short-
        // circuit and the table's outer height.
        var liveCount = 0;
        for (var i = 0; i < snapshot.Count; i++)
            if (!snapshot[i].IsDead) liveCount++;

        if (liveCount == 0)
        {
            ImGui.TextDisabled("No active spawns");
            return;
        }

        // Show one row when there's exactly one spawn; two rows when there
        // are two or more, with the remainder scrolling past the second
        // row. Using GetFrameHeightWithSpacing for the row height matches
        // ImGui's actual per-row layout when the row contains buttons (the
        // tallest content in each row sets the row height — our buttons).
        var rowsVisible  = Math.Clamp(liveCount, 1, 2);
        var headerHeight = ImGui.GetFrameHeight();
        var rowHeight    = ImGui.GetFrameHeightWithSpacing();
        var tableHeight  = headerHeight + rowHeight * rowsVisible;

        const ImGuiTableFlags flags =
            ImGuiTableFlags.RowBg       |
            ImGuiTableFlags.BordersInnerH |
            ImGuiTableFlags.ScrollY;

        using var table = ImRaii.Table("##faloopmicro_table", 3, flags,
            new Vector2(0f, tableHeight));
        if (!table.Success) return;

        ImGui.TableSetupColumn("Mob",     ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Time",    ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 168f);
        // Header row pins to the top of the scroll region even when the
        // user scrolls down — sticky header pattern.
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        for (var i = 0; i < snapshot.Count; i++)
        {
            var spawn = snapshot[i];
            if (spawn.IsDead) continue;
            DrawRow(spawn);
        }
    }

    private void DrawRow(SpawnInfo spawn)
    {
        var spawnKey = spawn.ReportedAt.Ticks;
        using var id = ImRaii.PushId($"micro_{spawnKey}");

        ImGui.TableNextRow();

        // ── Column 1: rank tag + mob · world (+ instance) ─────────────
        ImGui.TableNextColumn();

        // Dismiss × at the very start of the row, matching the standard
        // card's affordance for stale-entry cleanup. SmallButton keeps the
        // row height bounded.
        if (ImGui.SmallButton("×"))
        {
            _plugin.Client.RemoveSpawn(spawn);
            TeleportRoutine.ClearInProgress(spawnKey);
            return;
        }
        if (ImGui.IsItemHovered())
            using (ImRaii.Tooltip())
                ImGui.TextUnformatted("Remove this spawn");

        ImGui.SameLine(0f, 8f);

        // Rank chip — coloured to match the standard card's rank stripe.
        var rankCol = spawn.Rank switch
        {
            HuntRank.A => Theme.RankA,
            HuntRank.B => Theme.RankB,
            _          => Theme.RankS,
        };
        var rankLabel = spawn.IsSS ? "SS" : spawn.Rank.ToString();
        ImGui.TextColored(rankCol, rankLabel);

        ImGui.SameLine(0f, 8f);

        // Mob · world. Single line — no zone, no reporter (intentionally
        // sparse per user spec; full detail lives in the standard card).
        ImGui.TextUnformatted($"{spawn.MobName}  ·  {spawn.World}");

        // Instance badge inline if applicable.
        if (spawn.ZoneInstance > 0)
        {
            ImGui.SameLine(0f, 6f);
            ImGui.TextColored(Theme.TextTertiary, $"i{spawn.ZoneInstance}");
        }

        // Scheduled / phase / just-released signals as a colored dot
        // before the rank chip. Tooltip explains.
        // (Placed at the end of column 1 logic so the tooltip targets
        // whatever's hovered; ImGui's hover detection doesn't care about
        // the order we drew things in.)

        // ── Column 2: age ─────────────────────────────────────────────
        ImGui.TableNextColumn();
        var age = TimeSync.ServerNow - spawn.ReportedAt;
        ImGui.TextColored(Theme.TextTertiary, FormatAge(age));

        // ── Column 3: action buttons ──────────────────────────────────
        ImGui.TableNextColumn();
        var canAct  = spawn.TerritoryId > 0;
        var isTP    = TeleportRoutine.IsInProgress(spawnKey);
        var hasLife = TeleportRoutine.LifestreamAvailable;

        // Teleport — disabled when no territory or Lifestream missing.
        var tpLabel = !hasLife ? "—" : isTP ? "TP…" : "TP";
        using (ImRaii.Disabled(isTP || !canAct || !hasLife))
        {
            if (ImGui.Button($"{tpLabel}##tp", new Vector2(48f, 0)))
                TeleportRoutine.Teleport(spawn);
        }
        if (!hasLife && ImGui.IsItemHovered())
            using (ImRaii.Tooltip())
                ImGui.TextUnformatted("Teleport requires the Lifestream plugin.");

        ImGui.SameLine(0f, 4f);
        // Flag button turns red + disabled when the spawn has no confirmed
        // location yet (Faloop hasn't told us where the mob is — common for
        // scheduled / pre-release reports). Flips back to normal the frame
        // real coords arrive, since HasLocation is recomputed each draw.
        var hasLoc = spawn.HasLocation;
        if (!hasLoc)
        {
            using (ImRaii.PushColor(ImGuiCol.Button,        Theme.Danger))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Theme.Danger))
            using (ImRaii.PushColor(ImGuiCol.ButtonActive,  Theme.Danger))
            using (ImRaii.Disabled())
            {
                ImGui.Button("Flag##flag", new Vector2(52f, 0));
            }
            if (ImGui.IsItemHovered())
                using (ImRaii.Tooltip())
                    ImGui.TextUnformatted("Location not confirmed by Faloop yet.");
        }
        else
        {
            using (ImRaii.Disabled(!canAct))
            {
                if (ImGui.Button("Flag##flag", new Vector2(52f, 0)))
                    TeleportRoutine.SetFlag(spawn);
            }
        }

        ImGui.SameLine(0f, 4f);
        if (ImGui.Button("PF##pf", new Vector2(48f, 0)))
            TeleportRoutine.OpenPartyFinder();
    }

    // FormatAge mirrors the standard card so the micro column reads
    // consistently with everything else in the plugin: "5s", "3m", "2h",
    // "1d". One-letter unit suffixes keep the column narrow.
    private static string FormatAge(TimeSpan t)
    {
        if (t.TotalSeconds < 60)  return $"{(int)t.TotalSeconds}s";
        if (t.TotalMinutes < 60)  return $"{(int)t.TotalMinutes}m";
        if (t.TotalHours   < 24)  return $"{(int)t.TotalHours}h";
        return $"{(int)t.TotalDays}d";
    }
}
