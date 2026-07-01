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

    // Semantic text ramp for the redesigned card. Contrast-checked against
    // CardBg (≈AA): Primary ≈ white for the title (world), Secondary for the
    // mob name, Tertiary for meta (≥4.5:1 — Muted was ~3:1 and failed).
    public static readonly Vector4 TextPrimary   = new(0.95f, 0.96f, 0.98f, 1.00f);
    public static readonly Vector4 TextSecondary = new(0.80f, 0.82f, 0.87f, 1.00f);
    public static readonly Vector4 TextTertiary  = new(0.66f, 0.68f, 0.74f, 1.00f);

    // Instance badge — distinct, high-contrast pill (finding the mob depends
    // on the instance, so it must not read as muted).
    public static readonly Vector4 InstanceBg   = new(0.42f, 0.30f, 0.62f, 1.00f);
    public static readonly Vector4 InstanceText = new(0.97f, 0.95f, 1.00f, 1.00f);

    // Dedicated pull panel.
    public static readonly Vector4 PullPanelBg     = new(0.10f, 0.11f, 0.13f, 0.95f);
    public static readonly Vector4 PullPanelBorder = new(0.30f, 0.32f, 0.38f, 1.00f);

    // Neutral (secondary) button — used for everything except the primary
    // Teleport action, so the eye isn't asked to re-read three gold chips.
    public static readonly Vector4 BtnNeutral     = new(0.22f, 0.24f, 0.29f, 1f);
    public static readonly Vector4 BtnNeutralHov  = new(0.30f, 0.33f, 0.39f, 1f);
    public static readonly Vector4 BtnNeutralActv = new(0.38f, 0.41f, 0.48f, 1f);
    public static readonly Vector4 BtnNeutralText = new(0.88f, 0.90f, 0.94f, 1f);

    // Hunt-train pull timer: amber while counting down, bright green at "PULL".
    public static readonly Vector4 PullWait  = new(1.00f, 0.70f, 0.20f, 1.00f);
    public static readonly Vector4 PullReady = new(0.30f, 0.95f, 0.40f, 1.00f);

    // Gold button palette (used for Party/Ping/TP)
    public static readonly Vector4 BtnGold     = new(0.78f, 0.65f, 0.15f, 1f);
    public static readonly Vector4 BtnGoldHov  = new(0.92f, 0.78f, 0.20f, 1f);
    public static readonly Vector4 BtnGoldActv = new(1.00f, 0.85f, 0.15f, 1f);
    public static readonly Vector4 BtnGoldText = new(0.06f, 0.06f, 0.08f, 1f);

    // Danger red — used to flag a not-yet-plantable Flag button (spawn has
    // no confirmed coordinates from Faloop yet). Slightly desaturated from
    // the connection-status red so a disabled button reads as "waiting"
    // rather than "error".
    public static readonly Vector4 Danger = new(0.72f, 0.24f, 0.22f, 1f);
}
