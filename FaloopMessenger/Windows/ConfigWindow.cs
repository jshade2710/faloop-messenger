using System;
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

    public ConfigWindow(Plugin plugin) : base("Faloop · Settings###FaloopConfig")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(460, 380),
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

        var onlyS = Config.OnlySRanks;
        if (ImGui.Checkbox("Show S-ranks only", ref onlyS))
        {
            Config.OnlySRanks = onlyS;
            Config.Save();
        }
        ImGui.Spacing();

        var max = Config.MaxEntries;
        ImGui.SetNextItemWidth(80f);
        if (ImGui.InputInt("Max entries kept##max", ref max))
        {
            Config.MaxEntries = Math.Clamp(max, 10, 500);
            Config.Save();
        }

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "DISPLAY");
        ImGui.Separator();
        ImGui.Spacing();

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
    }

    // ── Notifications ────────────────────────────────────────────────

    private void DrawNotificationsTab()
    {
        ImGui.TextColored(Theme.Muted, "ON NEW SPAWN");
        ImGui.Separator();
        ImGui.Spacing();

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

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "MINI WINDOW  (/faloopmini)");
        ImGui.Separator();
        ImGui.Spacing();

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

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "PING BUTTON");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(
            "Ping prints a clickable map link to your local Echo chat. " +
            "Click the map thumbnail on a card to plant the in-game flag.");
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted,
            "Sending to other channels (party / yell / etc.) requires game " +
            "chat-send infrastructure that isn't implemented yet.");

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "TP BUTTON");
        ImGui.Separator();
        ImGui.Spacing();

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

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "TROUBLESHOOTING");
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(Theme.Warn,
            "If the tracker stays blank after connecting, enable Verbose logging");
        ImGui.TextColored(Theme.Muted,
            "in Dalamud settings and search the log for [Faloop] to see live events.");
    }
}
