using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FaloopMessenger.Windows;

// Companion window that just renders the current live S-rank list. The same
// class powers both `/faloopmini` (standard cards) and `/faloopcompact`
// (64 px cards) — the only difference is the `compact` flag and the default
// size. Auto-pops on new spawn and auto-closes when idle (gated by config).
public class SpawnListWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly bool   _compact;

    public SpawnListWindow(Plugin plugin, string title, bool compact, Vector2 defaultSize, Vector2 minSize)
        : base(title, ImGuiWindowFlags.NoCollapse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = minSize,
            MaximumSize = new Vector2(900, 1200),
        };
        SizeCondition = ImGuiCond.FirstUseEver;
        Size          = defaultSize;
        _plugin       = plugin;
        _compact      = compact;
    }

    public void Dispose() { }

    public override bool DrawConditions() => !Plugin.HiddenInInstance(_plugin.Configuration);

    public override void Draw()
    {
        // M-5 fix (v0.4.7 audit): show every tracked rank, not just S. With
        // v0.4.5+ per-rank tracking the user can opt into A-ranks, and the
        // mini/compact windows previously ignored those entries — making the
        // setting feel broken. Filtering already happens upstream (the socket
        // client drops anything outside the user's selected ranks), so we
        // just render whatever survived into _spawns.
        var spawns = _plugin.Client.GetSnapshot();

        var any = false;
        for (var i = 0; i < spawns.Count; i++)
            if (!spawns[i].IsDead) { any = true; break; }

        if (!any)
        {
            ImGui.TextDisabled("No active spawns");
            return;
        }

        for (var i = 0; i < spawns.Count; i++)
            if (!spawns[i].IsDead)
                SpawnCardRenderer.DrawCard(spawns[i], _plugin.Client, compact: _compact);
    }
}
