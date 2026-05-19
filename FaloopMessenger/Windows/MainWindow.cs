using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace FaloopMessenger.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private FaloopSocketClient Client => _plugin.Client;

    // Colour constants come from Theme.cs.

    public MainWindow(Plugin plugin) : base("Faloop##faloop", ImGuiWindowFlags.None)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(740, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        _plugin = plugin;
    }

    public void Dispose() { }

    // Suppress the window entirely (without closing it) while in an instanced
    // duty, if the user enabled that option.
    public override bool DrawConditions() => !Plugin.HiddenInInstance(_plugin.Configuration);

    public override void Draw()
    {
        DrawStatusBar();
        ImGui.Separator();
        ImGui.Spacing();
        DrawSpawnList(ImGui.GetContentRegionAvail().Y);
    }

    // ── Status bar ────────────────────────────────────────────────────

    private void DrawStatusBar()
    {
        var (stateCol, stateText) = Client.State switch
        {
            ConnectionState.Connected  => (Theme.Connected,  "Connected"),
            ConnectionState.Connecting => (Theme.Connecting, "Connecting..."),
            _                          => (Theme.Disconnected,    "Disconnected"),
        };

        var dl  = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        dl.AddCircleFilled(new Vector2(pos.X + 6f, pos.Y + 9f), 5f, ImGui.GetColorU32(stateCol));
        ImGui.Dummy(new Vector2(16f, 0f));
        ImGui.SameLine(0, 0f);
        ImGui.TextColored(stateCol, stateText);

        if (Client.LastError != null)
        {
            ImGui.SameLine(0, 8f);
            ImGui.TextColored(Theme.Muted, $"({Client.LastError})");
        }

        const float BtnW = 90f, BtnW2 = 64f, Gap = 6f, Pad = 16f;
        ImGui.SameLine(ImGui.GetWindowWidth() - BtnW - BtnW2 - Gap - Pad);

        if (ImGui.Button("Reconnect", new Vector2(BtnW, 0)))
            Client.Reconnect();

        ImGui.SameLine(0, Gap);
        if (ImGui.Button("Settings", new Vector2(BtnW2, 0)))
            _plugin.ToggleConfigUi();
    }

    // ── Spawn list ────────────────────────────────────────────────────

    private void DrawSpawnList(float availH)
    {
        // Cached immutable snapshot — zero per-frame allocation. Iterated
        // directly (no LINQ/ToArray) so the draw loop doesn't churn the GC.
        var spawns = Client.GetSnapshot();

        using var child = ImRaii.Child("##spawnList", new Vector2(-1f, availH), false);
        if (!child.Success) return;

        if (spawns.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(Client.State == ConnectionState.Connected
                ? "No spawns received yet — waiting for reports on Faloop."
                : "Not connected. Click Reconnect or check Settings.");
            return;
        }

        // Pass 1: live cards.
        for (var i = 0; i < spawns.Count; i++)
            if (!spawns[i].IsDead)
                SpawnCardRenderer.DrawCard(spawns[i], Client, compact: false);

        // Pass 2: up to 8 recently-killed rows, header drawn lazily on first.
        var deadShown = 0;
        for (var i = 0; i < spawns.Count && deadShown < 8; i++)
        {
            if (!spawns[i].IsDead) continue;
            if (deadShown == 0)
            {
                ImGui.Spacing();
                using (Plugin.FontMedium.Push())
                    ImGui.TextColored(Theme.Subtle, "RECENTLY KILLED");
                ImGui.Separator();
                ImGui.Spacing();
            }
            SpawnCardRenderer.DrawDeadRow(spawns[i]);
            deadShown++;
        }
    }
}
