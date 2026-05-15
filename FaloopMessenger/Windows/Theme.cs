using System.Numerics;

namespace FaloopMessenger.Windows;

// One place for every colour the plugin uses. Per-window colour constants
// previously lived in every Window class — kept getting out of sync. Now
// everything reads from here.
internal static class Theme
{
    // Hunt-rank accent colours (badge / stripe / mob name)
    public static readonly Vector4 RankS = new(1.00f, 0.85f, 0.15f, 1f);  // gold
    public static readonly Vector4 RankA = new(1.00f, 0.55f, 0.15f, 1f);  // orange
    public static readonly Vector4 RankB = new(0.50f, 0.75f, 1.00f, 1f);  // blue

    // Connection-status indicators
    public static readonly Vector4 Connected    = new(0.20f, 0.85f, 0.30f, 1f);
    public static readonly Vector4 Connecting   = new(0.90f, 0.80f, 0.10f, 1f);
    public static readonly Vector4 Disconnected = new(0.90f, 0.25f, 0.20f, 1f);

    // Card surface colours
    public static readonly Vector4 CardBg    = new(0.13f, 0.15f, 0.18f, 0.95f);
    public static readonly Vector4 CardBgHov = new(0.17f, 0.20f, 0.24f, 0.95f);
    public static readonly Vector4 ChipBg   = new(0.25f, 0.28f, 0.34f, 1.00f);

    // Text emphasis levels
    public static readonly Vector4 Muted     = new(0.55f, 0.55f, 0.55f, 1.00f);
    public static readonly Vector4 Subtle    = new(0.70f, 0.72f, 0.78f, 1.00f);
    public static readonly Vector4 RouteHint = new(0.78f, 0.85f, 1.00f, 1.00f);
    public static readonly Vector4 AgeFresh  = new(0.45f, 0.95f, 1.00f, 1.00f);
    public static readonly Vector4 Warn      = new(1.00f, 0.75f, 0.10f, 1.00f);

    // Hunt-train pull timer: amber while counting down, bright green at "PULL".
    public static readonly Vector4 PullWait  = new(1.00f, 0.70f, 0.20f, 1.00f);
    public static readonly Vector4 PullReady = new(0.30f, 0.95f, 0.40f, 1.00f);

    // Gold button palette (used for Party/Ping/TP)
    public static readonly Vector4 BtnGold     = new(0.78f, 0.65f, 0.15f, 1f);
    public static readonly Vector4 BtnGoldHov  = new(0.92f, 0.78f, 0.20f, 1f);
    public static readonly Vector4 BtnGoldActv = new(1.00f, 0.85f, 0.15f, 1f);
    public static readonly Vector4 BtnGoldText = new(0.06f, 0.06f, 0.08f, 1f);
}
