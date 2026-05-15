using System;
using System.Linq;
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

    public override bool DrawConditions() => !Plugin.HiddenForCombat(_plugin.Configuration);

    public override void Draw()
    {
        var live = _plugin.Client.GetSnapshot()
            .Where(s => !s.IsDead && s.Rank == HuntRank.S)
            .ToArray();

        if (live.Length == 0)
        {
            ImGui.TextDisabled("No active S-ranks");
            return;
        }

        foreach (var s in live)
            SpawnCardRenderer.DrawCard(s, _plugin.Client, compact: _compact);
    }
}
