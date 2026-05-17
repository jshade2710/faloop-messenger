using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace FaloopMessenger.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private Configuration Config => _plugin.Configuration;

    private bool _showPassword;

    private static readonly string[] DcOptions = { "All", "Aether" };

    // Expansion → display label for the per-expansion filter checklist.
    private static readonly (Expansion exp, string label)[] Expansions =
    {
        (Expansion.ARR, "A Realm Reborn"),
        (Expansion.HW,  "Heavensward"),
        (Expansion.StB, "Stormblood"),
        (Expansion.ShB, "Shadowbringers"),
        (Expansion.EW,  "Endwalker"),
        (Expansion.DT,  "Dawntrail"),
    };

    public ConfigWindow(Plugin plugin) : base("Faloop · Settings###FaloopConfig")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 460),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        _plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        using var tabBar = ImRaii.TabBar("##cfg_tabs");
        if (!tabBar.Success) return;

        Tab("Account",       DrawAccountTab);
        Tab("Filters",       DrawFiltersTab);
        Tab("Notifications", DrawNotificationsTab);
        Tab("Advanced",      DrawAdvancedTab);
    }

    private static void Tab(string label, Action body)
    {
        using var tab = ImRaii.TabItem(label);
        if (!tab.Success) return;
        ImGui.Spacing();
        body();
    }

    // Worlds belonging to the given data center, resolved to display names via
    // Lumina and sorted alphabetically. "All"/empty DC falls back to Aether
    // (the only DC we have a verified world set for). Runs on the framework
    // (draw) thread, so the Lumina lookup is safe here.
    private static (string label, int key)[] WorldsForDc(string dc)
    {
        var key = string.IsNullOrEmpty(dc) || dc.Equals("All", StringComparison.OrdinalIgnoreCase)
            ? "Aether"
            : dc;
        if (!FaloopData.DataCenters.TryGetValue(key, out var ids))
            return Array.Empty<(string, int)>();

        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>();
        return ids
            .Select(id => (
                label: sheet != null && sheet.TryGetRow(id, out var row)
                    ? row.Name.ToString()
                    : id.ToString(),
                key: (int)id))
            .OrderBy(w => w.label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Shared always-visible multi-select picker used for both the world and
    // expansion filters. The list stays open (no reveal-on-checkbox); an
    // "enabled" flag disambiguates the empty whitelist:
    //   • disabled            → no filter, every item shows checked
    //   • enabled, whitelist  → only checked items notify
    //   • enabled, empty list → "None" (nothing notifies — explicit choice)
    // Unchecking an item while unfiltered materialises the full set first, and
    // re-checking everything collapses back to the clean "no filter" state.
    private void FilterPanel(
        string idTag, string title, string tooltip,
        (string label, int key)[] items,
        Func<bool> enabledGet, Action<bool> enabledSet,
        List<int> whitelist)
    {
        ImGui.TextColored(Theme.Muted, title);
        ImGui.SameLine(0, 6f);
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);

        var enabled = enabledGet();
        var rows    = (items.Length + 1) / 2;
        var height  = ImGui.GetFrameHeightWithSpacing() * (rows + 1) + 14f;

        using var child = ImRaii.Child($"##panel_{idTag}", new Vector2(-1f, height), true);
        if (!child.Success) return;

        if (ImGui.SmallButton($"All##{idTag}"))
        {
            enabledSet(false);
            whitelist.Clear();
            Config.Save();
            enabled = false;
        }
        ImGui.SameLine(0, 6f);
        if (ImGui.SmallButton($"None##{idTag}"))
        {
            enabledSet(true);
            whitelist.Clear();
            Config.Save();
            enabled = true;
        }
        ImGui.SameLine(0, 12f);
        ImGui.TextColored(Theme.Muted,
            !enabled            ? "all (no filter)"
            : whitelist.Count == 0 ? "none selected"
            : $"filtering · {whitelist.Count} selected");

        for (var i = 0; i < items.Length; i++)
        {
            var (label, key) = items[i];
            var on = !enabled || whitelist.Contains(key);
            if (ImGui.Checkbox($"{label}##{idTag}{key}", ref on))
            {
                // First customisation from the unfiltered state: snapshot the
                // full set so a single uncheck means "all except this".
                if (!enabled)
                {
                    enabledSet(true);
                    whitelist.Clear();
                    whitelist.AddRange(items.Select(x => x.key));
                }

                if (on) { if (!whitelist.Contains(key)) whitelist.Add(key); }
                else    whitelist.Remove(key);

                // Everything re-checked → collapse to the clean no-filter state.
                if (items.All(x => whitelist.Contains(x.key)))
                {
                    enabledSet(false);
                    whitelist.Clear();
                }
                Config.Save();
            }
            if (i % 2 == 0 && i + 1 < items.Length)
                ImGui.SameLine(190f);
        }
    }

    // ── Account ──────────────────────────────────────────────────────

    private void DrawAccountTab()
    {
        ImGui.TextWrapped(
            "Anonymous sessions can connect but may not receive live spawn pushes. " +
            "Enter your faloop.app credentials to receive real-time events.");
        ImGui.Spacing();

        ImGui.TextColored(Theme.Muted, "Username");
        var user = Config.Username;
        ImGui.SetNextItemWidth(280f);
        if (ImGui.InputText("##user", ref user, 128))
        {
            Config.Username = user;
            Config.Save();
        }

        ImGui.TextColored(Theme.Muted, "Password");
        var pass = Config.Password;
        ImGui.SetNextItemWidth(280f);
        var flags = _showPassword ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        if (ImGui.InputText("##pass", ref pass, 128, flags))
        {
            Config.Password = pass;
            Config.Save();
        }
        ImGui.SameLine(0, 8f);
        ImGui.Checkbox("Show##showpass", ref _showPassword);

        ImGui.Spacing();
        if (ImGui.Button("Apply & Reconnect", new Vector2(160f, 0f)))
            _plugin.Client.Reconnect();
    }

    // ── Filters ──────────────────────────────────────────────────────

    private void DrawFiltersTab()
    {
        // ── SCOPE ─────────────────────────────────────────────────────
        Section("SCOPE — which spawns reach you");

        ImGui.TextColored(Theme.Muted, "Data center");
        var currentDc = string.IsNullOrEmpty(Config.DataCenter) ? "All" : Config.DataCenter;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.BeginCombo("##dc", currentDc))
        {
            foreach (var name in DcOptions)
            {
                var selected = name.Equals(currentDc, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(name, selected))
                {
                    Config.DataCenter = name;
                    Config.Save();
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.Spacing();

        var worlds = WorldsForDc(Config.DataCenter);
        if (worlds.Length == 0)
            ImGui.TextColored(Theme.Muted, "No world list for this data center.");
        else
            FilterPanel(
                "worlds", "Worlds",
                "Tick the worlds you want notifications from (e.g. just\n" +
                "Gilgamesh + Sargatanas). \"All\" = the whole data center.",
                worlds,
                () => Config.WorldFilterEnabled,
                v  => Config.WorldFilterEnabled = v,
                Config.WorldWhitelist);

        ImGui.Spacing();

        FilterPanel(
            "exp", "Expansions",
            "Tick the expansions you want notifications from — e.g. only\n" +
            "Dawntrail to ignore older-expansion hunt trains.",
            Expansions.Select(e => (e.label, (int)e.exp)).ToArray(),
            () => Config.ExpansionFilterEnabled,
            v  => Config.ExpansionFilterEnabled = v,
            Config.ExpansionWhitelist);

        // ── HUNTS ─────────────────────────────────────────────────────
        Section("HUNTS");

        var onlyS = Config.OnlySRanks;
        if (ImGui.Checkbox("Show S-ranks only", ref onlyS))
        {
            Config.OnlySRanks = onlyS;
            Config.Save();
        }
        ImGui.SameLine(0, 6f);
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When off, A and B ranks are tracked too (still DC/world/expansion filtered).");

        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "Max entries kept");
        ImGui.SetNextItemWidth(80f);
        var max = Config.MaxEntries;
        if (ImGui.InputInt("##max", ref max))
        {
            Config.MaxEntries = Math.Clamp(max, 10, 500);
            Config.Save();
        }

        // ── DISPLAY ───────────────────────────────────────────────────
        Section("DISPLAY");

        var hideInst = Config.HideInInstance;
        if (ImGui.Checkbox("Hide tracker windows in instanced duties", ref hideInst))
        {
            Config.HideInInstance = hideInst;
            Config.Save();
        }
        ImGui.SameLine(0, 6f);
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "When enabled, the /faloop, /faloopmini and /faloopcompact windows\n" +
                "are hidden while you're in an instanced duty (dungeon, trial,\n" +
                "raid, deep dungeon, variant dungeon) and reappear automatically\n" +
                "when you leave. Open-world combat — including fighting the S-rank\n" +
                "itself — does NOT hide them. They stay 'open', just not drawn.");

        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "Pull timer (minutes, 0 = off)");
        var pull = Config.PullTimerMinutes;
        ImGui.SetNextItemWidth(80f);
        if (ImGui.InputInt("##pulltimer", ref pull))
        {
            Config.PullTimerMinutes = Math.Clamp(pull, 0, 60);
            Config.Save();
        }
        ImGui.SameLine(0, 6f);
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Hunt-train convention: wait this many real-time minutes after a\n" +
                "spawn is reported before pulling. The card shows a countdown\n" +
                "(\"pull in 1m23s · 07:30 ET\", amber) — the ET value is the\n" +
                "Eorzean clock time the pull is due, so you can watch the in-game\n" +
                "clock. It flips to a green \"PULL\" when the wait is up. Set to 0\n" +
                "to hide the timer entirely.");
    }

    // Consistent section divider used across the tabs.
    private static void Section(string label)
    {
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, label);
        ImGui.Separator();
        ImGui.Spacing();
    }

    // ── Notifications ────────────────────────────────────────────────

    private void DrawNotificationsTab()
    {
        Section("ON NEW SPAWN");

        var autoEcho = Config.AutoEchoOnSpawn;
        if (ImGui.Checkbox("Print to chat (with clickable map link)", ref autoEcho))
        {
            Config.AutoEchoOnSpawn = autoEcho;
            Config.Save();
        }

        var autoSound = Config.AutoSoundOnSpawn;
        if (ImGui.Checkbox("Play sound effect", ref autoSound))
        {
            Config.AutoSoundOnSpawn = autoSound;
            Config.Save();
        }

        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "Sound effect (1–16, matches in-game <se.N>)");
        var se = (int)Config.SoundEffect;
        ImGui.SetNextItemWidth(80f);
        if (ImGui.InputInt("##se", ref se))
        {
            Config.SoundEffect = (uint)Math.Clamp(se, 1, 16);
            Config.Save();
        }
        ImGui.SameLine(0, 8f);
        if (ImGui.Button("Test##se-test"))
            Plugin.PlayChatSound(Config.SoundEffect);

        Section("MINI WINDOW  (/faloopmini)");

        var autoOpen = Config.AutoOpenMiniOnSpawn;
        if (ImGui.Checkbox("Auto-open when an S-rank spawns", ref autoOpen))
        {
            Config.AutoOpenMiniOnSpawn = autoOpen;
            Config.Save();
        }

        var autoClose = Config.AutoCloseMiniWhenIdle;
        if (ImGui.Checkbox("Auto-close when no S-ranks are live", ref autoClose))
        {
            Config.AutoCloseMiniWhenIdle = autoClose;
            Config.Save();
        }

        Section("PING BUTTON");

        ImGui.TextWrapped(
            "Ping prints a clickable map link to your local Echo chat. " +
            "Click the map thumbnail on a card to plant the in-game flag.");
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted,
            "Sending to other channels (party / yell / etc.) requires game " +
            "chat-send infrastructure that isn't implemented yet.");

        Section("TP BUTTON");

        ImGui.TextWrapped(
            "TP uses Lifestream to switch worlds (if needed) and teleport to " +
            "the closest aetheryte. Faloop's route data tells us when to " +
            "teleport to an adjacent zone's aetheryte instead (e.g. → " +
            "Idyllshire to walk into The Dravanian Hinterlands).");
        ImGui.Spacing();
        ImGui.TextColored(Theme.Warn,
            "The TP button requires the Lifestream plugin to be installed " +
            "and enabled. Without it the button does nothing.");
        ImGui.Spacing();

        ImGui.TextColored(Theme.Muted, "Install Lifestream:");
        ImGui.BulletText("Dalamud → ⚙ Settings → Experimental tab");
        ImGui.BulletText("Add this URL to \"Custom Plugin Repositories\":");
        var repoUrl = "https://love.puni.sh/ment.json";
        ImGui.Indent(20f);
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##repo-url", ref repoUrl, 128, ImGuiInputTextFlags.ReadOnly);
        if (ImGui.SmallButton("Copy URL##copyrepo"))
            ImGui.SetClipboardText("https://love.puni.sh/ment.json");
        ImGui.SameLine(0, 8f);
        if (ImGui.SmallButton("Open Lifestream on GitHub"))
            Dalamud.Utility.Util.OpenLink("https://github.com/NightmareXIV/Lifestream");
        ImGui.Unindent(20f);
        ImGui.BulletText("Save & close, then install \"Lifestream\" from the plugin list.");
    }

    // ── Advanced ─────────────────────────────────────────────────────

    private void DrawAdvancedTab()
    {
        ImGui.TextColored(Theme.Muted, "Socket URL");
        var url = Config.SocketUrl;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##url", ref url, 512))
        {
            Config.SocketUrl = url;
            Config.Save();
        }

        Section("TROUBLESHOOTING");

        ImGui.TextColored(Theme.Warn,
            "If the tracker stays blank after connecting, enable Verbose logging");
        ImGui.TextColored(Theme.Muted,
            "in Dalamud settings and search the log for [Faloop] to see live events.");
    }
}
