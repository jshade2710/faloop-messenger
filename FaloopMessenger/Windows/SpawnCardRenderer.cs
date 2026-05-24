using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;

namespace FaloopMessenger.Windows;

// Renders spawn cards in standard (116 px, full info) or compact (64 px) form.
// Static — owns its own animation/teleport state so every window (Main, Mini,
// Compact) sees the same fade-in timing and TP loading state.
internal static class SpawnCardRenderer
{
    // ── Per-spawn state (shared across windows) ───────────────────────
    private static readonly Dictionary<long, DateTime> _firstRenderAt = new();

    // All colour constants live in Theme.cs.

    // ── Layout constants ──────────────────────────────────────────────
    // All pixel constants flow through Scale (Configuration.UiScale, clamped
    // 0.8–1.5), so the entire card grows/shrinks proportionally — fonts,
    // heights, paddings, badges, buttons, markers, gaps. Inline pixel offsets
    // throughout the renderer go through the `S(v)` helper for the same
    // reason. Fractions (ThumbZoom) and time (FadeMs) are scale-free.
    internal static float Scale =>
        Math.Clamp(Plugin.Config?.UiScale ?? 1f, 0.8f, 1.5f);
    private static float S(float v) => v * Scale;

    private static float StdCardHeight   => 120f * Scale;
    private static float StdCardPad      => 10f  * Scale;
    private static float StdStripeWidth  => 4f   * Scale;
    private static float StdBadgeRadius  => 23f  * Scale;
    private static float StdColW         => 86f  * Scale;
    private static float StdChipH        => 26f  * Scale;
    private static float StdBtnH         => 22f  * Scale;
    private static float StdBtnGap       => 5f   * Scale;
    private static float StdMapTarget    => 100f * Scale;
    private static float StdMapMin       => 56f  * Scale;
    private static float StdTextMin      => 196f * Scale;

    private static float CmpCardHeight   => 70f * Scale;
    private static float CmpCardPad      => 7f  * Scale;
    private static float CmpStripeWidth  => 3f  * Scale;
    private static float CmpBadgeRadius  => 13f * Scale;
    private static float CmpButtonW      => 56f * Scale;
    private static float CmpBtnH         => 22f * Scale;
    private static float CmpBtnGap       => 4f  * Scale;

    private static float CardRound       => 6f * Scale;
    private const  float ThumbZoom       = 0.25f;   // fraction of texture
    private const  float FadeMs          = 600f;    // intro flash duration (ms)

    // ── Fixed-size text ───────────────────────────────────────────────
    // The card is pixel-static regardless of Dalamud's global font scale:
    // every text draw goes through ImDrawList's explicit-size AddText
    // overload, which renders at exactly the given px (the global scale
    // only affects the *implicit* current-font size). Fonts are locked
    // once per DrawCard to obtain their ImFontPtr.
    // Base medium bumped 16 → 18 for comfortable default readability;
    // every Px* scales with the user UI scale just like layout.
    private static float PxWorld   => 28f * Scale;
    private static float PxTitle   => 22f * Scale;
    private static float PxMedium  => 18f * Scale;

    private static ImFontPtr _fWorld, _fTitle, _fMedium;

    // Measure a string as if rendered at exactly `px`, independent of the
    // global font scale: push the font, measure at its current size, then
    // ratio down to px (the same px we hand to the explicit-size AddText).
    private static Vector2 Measure(ImFontPtr font, float px, string s)
    {
        ImGui.PushFont(font);
        var k = px / ImGui.GetFontSize();
        var w = ImGui.CalcTextSize(s);
        ImGui.PopFont();
        return w * k;
    }

    private static void DrawText(ImDrawListPtr dl, ImFontPtr font, float px,
                                 Vector2 pos, uint col, string s)
        => dl.AddText(font, px, pos, col, s);

    // ── Public entry ──────────────────────────────────────────────────

    public static void DrawCard(SpawnInfo spawn, FaloopSocketClient client, bool compact)
    {
        // FontAtlas rebuilds are async — when RebuildFonts() fires from the
        // UI Scale slider, .Lock() throws "did not built the requested handle
        // yet" for one or more frames. Catch and skip the card this frame;
        // the next frame after the atlas finishes will draw it cleanly.
        Dalamud.Interface.ManagedFontAtlas.ILockedImFont? lw = null, lt = null, lm = null;
        try
        {
            lw = Plugin.FontWorld.Lock();
            lt = Plugin.FontTitle.Lock();
            lm = Plugin.FontMedium.Lock();
        }
        catch (InvalidOperationException) { lw?.Dispose(); lt?.Dispose(); lm?.Dispose(); return; }

        using (lw) using (lt) using (lm)
        {
            _fWorld = lw.ImFont; _fTitle = lt.ImFont; _fMedium = lm.ImFont;

            if (compact) DrawCompactCard(spawn, client);
            else         DrawStandardCard(spawn, client);
        }
    }

    public static void DrawDeadRow(SpawnInfo spawn)
    {
        Dalamud.Interface.ManagedFontAtlas.ILockedImFont? lm;
        try { lm = Plugin.FontMedium.Lock(); }
        catch (InvalidOperationException) { return; }
        using (lm)
        {
        _fMedium = lm.ImFont;

        var DeadH = S(30f);
        var origin = ImGui.GetCursorScreenPos();
        var width  = ImGui.GetContentRegionAvail().X;
        var dl     = ImGui.GetWindowDrawList();

        dl.AddRectFilled(origin, origin + new Vector2(width, DeadH),
            ImGui.GetColorU32(new Vector4(0.10f, 0.11f, 0.13f, 0.5f)), CardRound);
        dl.AddRectFilled(origin, origin + new Vector2(StdStripeWidth, DeadH),
            ImGui.GetColorU32(Theme.Muted), CardRound);

        var badgeC = new Vector2(origin.X + StdStripeWidth + S(14f), origin.Y + DeadH / 2f);
        var mutedU = ImGui.GetColorU32(Theme.Muted);
        dl.AddCircle(badgeC, S(8f), mutedU, 0, 1.5f);
        var xs = Measure(_fMedium, PxMedium, "✗");
        DrawText(dl, _fMedium, PxMedium, badgeC - xs * 0.5f, mutedU, "✗");

        var killedAt = spawn.KilledAt ?? TimeSync.ServerNow;
        var age      = TimeSync.ServerNow - killedAt;
        // World-first, mirroring the live card's hierarchy.
        var parts    = new List<string> { spawn.World, spawn.MobName, ZoneLabel(spawn) };
        if (!string.IsNullOrEmpty(spawn.Reporter)) parts.Add($"by {spawn.Reporter}");
        parts.Add($"killed {FormatAge(age)} ago");
        var text = string.Join("  ·  ", parts);

        var textY = origin.Y + (DeadH - PxMedium) / 2f;
        DrawText(dl, _fMedium, PxMedium, new Vector2(badgeC.X + S(14f), textY),
            ImGui.GetColorU32(Theme.TextTertiary), text);

        ImGui.Dummy(new Vector2(width, DeadH));
        ImGui.Spacing();
        }
    }

    // ── Standard 116 px card ─────────────────────────────────────────

    private static void DrawStandardCard(SpawnInfo spawn, FaloopSocketClient client)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width  = ImGui.GetContentRegionAvail().X;
        var dl     = ImGui.GetWindowDrawList();

        var (rankCol, rankU32, fresh, fadeFrac, spawnKey) = PrepCard(spawn);

        using (ImRaii.PushId($"card_{spawnKey}"))
        {
            var hovered = ImGui.IsMouseHoveringRect(origin, origin + new Vector2(width, StdCardHeight));

            var bgNormal = hovered ? Theme.CardBgHov : Theme.CardBg;
            var bgFlash  = new Vector4(rankCol.X, rankCol.Y, rankCol.Z, 0.35f);
            var bg       = Vector4.Lerp(bgFlash, bgNormal, fadeFrac);
            dl.AddRectFilled(origin, origin + new Vector2(width, StdCardHeight),
                ImGui.GetColorU32(bg), CardRound);
            dl.AddRectFilled(origin, origin + new Vector2(StdStripeWidth, StdCardHeight),
                rankU32, CardRound);

            // × dismiss (top-left, just inside the stripe — kept here per an
            // earlier explicit request).
            if (DrawCloseButton(origin, StdStripeWidth + S(4f), S(5f), S(14f), spawnKey, dl))
            {
                client.RemoveSpawn(spawn);
                _firstRenderAt.Remove(spawnKey);
                TeleportRoutine.InProgress.Remove(spawnKey);
                return;
            }

            // "{}" — copy this spawn's raw Faloop JSON.
            if (DrawRawButton(origin, StdStripeWidth + S(22f), S(4f), spawnKey, dl))
                OutputRawJson(spawn);

            // Rank = accent identity ONLY (stripe + badge), vertically centred
            // and enlarged so "S" reads at a glance.
            var badgeCenter = new Vector2(
                origin.X + StdStripeWidth + S(11f) + StdBadgeRadius,
                origin.Y + StdCardHeight / 2f);
            DrawRankBadge(badgeCenter, StdBadgeRadius,
                spawn.IsSS ? "SS" : spawn.Rank.ToString(),
                rankU32, fresh, dl, useTitleFont: true);

            // ── Fixed right column: minimal timer chip + 3 buttons ─────
            var pull = PullState(spawn);
            var colX = origin.X + width - S(8f) - StdColW;

            var btnTotalH = StdBtnH * 3f + StdBtnGap * 2f;
            var stackH    = pull.Enabled ? StdChipH + S(8f) + btnTotalH : btnTotalH;
            var colY      = origin.Y + (StdCardHeight - stackH) / 2f;

            if (pull.Enabled)
            {
                DrawTimerChip(dl, new Vector2(colX, colY), new Vector2(StdColW, StdChipH),
                              pull, spawnKey);
                colY += StdChipH + S(8f);
            }
            DrawActionButtonsStacked(dl, spawn, colX, colY, StdColW, StdBtnH, StdBtnGap, spawnKey);

            // ── Fluid columns: text has a floor + grows; the map is the
            //    elastic element (shrinks, then collapses on a narrow window).
            var textX     = origin.X + StdStripeWidth + S(13f) + StdBadgeRadius * 2f + S(16f);
            var regionR   = colX - S(10f);
            var room      = regionR - (textX + StdTextMin);   // space available to the map
            var mapS      = MathF.Min(StdMapTarget, MathF.Min(room, StdCardHeight - 2f * StdCardPad));
            if (mapS < StdMapMin) mapS = 0f;                   // collapse breakpoint

            float textRight;
            if (mapS > 0f)
            {
                var mapX = regionR - mapS;
                DrawMapThumb(spawn,
                    new Vector2(mapX, origin.Y + (StdCardHeight - mapS) / 2f), mapS);
                textRight = mapX - S(12f);
            }
            else
            {
                textRight = regionR;
            }

            // ── Text block (fixed rows; ellipsis only as a last resort) ─
            var y = origin.Y + StdCardPad;

            // Row 1 — WORLD (title) + instance badge
            {
                var badgeW = spawn.ZoneInstance > 0
                    ? MeasureInstanceBadge(spawn.ZoneInstance) + S(10f)
                    : 0f;
                var endX = DrawClipped(dl, _fWorld, PxWorld, new Vector2(textX, y),
                    ImGui.GetColorU32(Theme.TextPrimary), spawn.World,
                    textRight - textX - badgeW);
                if (spawn.ZoneInstance > 0)
                    DrawInstanceBadge(dl, new Vector2(endX + S(10f), y + S(6f)), spawn.ZoneInstance);
                y += PxWorld + S(1f);
            }

            // Row 2 — mob name (secondary)
            DrawClipped(dl, _fMedium, PxMedium, new Vector2(textX, y),
                ImGui.GetColorU32(Theme.TextSecondary), spawn.MobName,
                textRight - textX);
            y += PxMedium + S(4f);

            // Row 3 — meta strip (priority-degrading; clips the live segment)
            y = DrawMetaRow(dl, spawn, fresh, new Vector2(textX, y), textRight);

            // Row 4 — route hint (fixed slot; blank if none, never reflows)
            DrawRouteHintClipped(spawn, new Vector2(textX, y), dl, textRight - textX);
        }

        ImGui.SetCursorScreenPos(origin + new Vector2(0f, StdCardHeight + S(6f)));
    }

    // ── Compact 64 px card ───────────────────────────────────────────

    private static void DrawCompactCard(SpawnInfo spawn, FaloopSocketClient client)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width  = ImGui.GetContentRegionAvail().X;
        var dl     = ImGui.GetWindowDrawList();

        var (rankCol, rankU32, fresh, fadeFrac, spawnKey) = PrepCard(spawn);

        using (ImRaii.PushId($"ccard_{spawnKey}"))
        {
            var hovered = ImGui.IsMouseHoveringRect(origin, origin + new Vector2(width, CmpCardHeight));

            var bgNormal = hovered ? Theme.CardBgHov : Theme.CardBg;
            var bgFlash  = new Vector4(rankCol.X, rankCol.Y, rankCol.Z, 0.35f);
            var bg       = Vector4.Lerp(bgFlash, bgNormal, fadeFrac);
            dl.AddRectFilled(origin, origin + new Vector2(width, CmpCardHeight),
                ImGui.GetColorU32(bg), CardRound);
            dl.AddRectFilled(origin, origin + new Vector2(CmpStripeWidth, CmpCardHeight),
                rankU32, CardRound);

            if (DrawCloseButton(origin, CmpStripeWidth + S(3f), S(3f), S(12f), spawnKey, dl))
            {
                client.RemoveSpawn(spawn);
                _firstRenderAt.Remove(spawnKey);
                TeleportRoutine.InProgress.Remove(spawnKey);
                return;
            }

            if (DrawRawButton(origin, CmpStripeWidth + S(19f), S(2f), spawnKey, dl))
                OutputRawJson(spawn);

            var badgeCenter = new Vector2(
                origin.X + CmpStripeWidth + S(8f) + CmpBadgeRadius, origin.Y + CmpCardHeight / 2f);
            DrawRankBadge(badgeCenter, CmpBadgeRadius,
                spawn.IsSS ? "SS" : spawn.Rank.ToString(),
                rankU32, fresh, dl, useTitleFont: false);

            // Same field order as the standard card (just denser). Minimal
            // timer chip on the right, then the buttons.
            var pull   = PullState(spawn);
            var btnW   = CmpButtonW * 3 + CmpBtnGap * 2;
            var chipW  = pull.Enabled ? S(78f) : 0f;   // fits "ET HH:MM"
            var chipX  = origin.X + width - S(8f) - chipW;
            var btnX0  = chipX - (pull.Enabled ? S(10f) : 0f) - btnW;

            if (pull.Enabled)
                DrawTimerChip(dl,
                    new Vector2(chipX, origin.Y + (CmpCardHeight - S(24f)) / 2f),
                    new Vector2(chipW, S(24f)), pull, spawnKey);

            var textX     = origin.X + CmpStripeWidth + S(8f) + CmpBadgeRadius * 2f + S(12f);
            var textRight = btnX0 - S(12f);
            var y         = origin.Y + CmpCardPad;

            // Row 1 — WORLD (title) + instance badge + trailing mob name,
            //         each clipped so nothing overruns the chip/buttons.
            {
                var cursorX = DrawClipped(dl, _fMedium, PxMedium, new Vector2(textX, y),
                    ImGui.GetColorU32(Theme.TextPrimary), spawn.World,
                    (textRight - textX) * 0.55f);
                cursorX += S(8f);
                if (spawn.ZoneInstance > 0)
                {
                    cursorX = DrawInstanceBadge(dl, new Vector2(cursorX, y + S(1f)),
                        spawn.ZoneInstance) + S(8f);
                }
                DrawClipped(dl, _fMedium, PxMedium, new Vector2(cursorX, y + S(2f)),
                    ImGui.GetColorU32(Theme.TextSecondary), spawn.MobName,
                    textRight - cursorX);
                y += PxMedium + S(4f);
            }

            // Row 2 — meta strip (same builder as the standard card)
            DrawMetaRow(dl, spawn, fresh, new Vector2(textX, y), textRight, compact: true);

            // Actions: primary + neutral secondaries, vertically centred
            var aBtnY = origin.Y + (CmpCardHeight - CmpBtnH) / 2f;
            DrawActionButtonsCompact(dl, spawn, btnX0, aBtnY, spawnKey);
        }

        ImGui.SetCursorScreenPos(origin + new Vector2(0f, CmpCardHeight + S(4f)));
    }

    // ── Shared helpers ────────────────────────────────────────────────

    private static (Vector4 col, uint u32, bool fresh, float fade, long key) PrepCard(SpawnInfo spawn)
    {
        var col = spawn.Rank switch
        {
            HuntRank.A => Theme.RankA,
            HuntRank.B => Theme.RankB,
            _          => Theme.RankS,
        };
        var fresh    = (TimeSync.ServerNow - spawn.ReportedAt).TotalSeconds < 180;
        var spawnKey = spawn.ReportedAt.Ticks;

        if (!_firstRenderAt.TryGetValue(spawnKey, out var renderedAt))
        {
            renderedAt = DateTime.Now;
            _firstRenderAt[spawnKey] = renderedAt;
        }
        var fadeFrac = MathF.Min(1f, (float)((DateTime.Now - renderedAt).TotalMilliseconds / FadeMs));

        return (col, ImGui.GetColorU32(col), fresh, fadeFrac, spawnKey);
    }

    // Returns true if the close button was clicked this frame.
    private static bool DrawCloseButton(Vector2 origin, float dx, float dy, float size,
                                        long spawnKey, ImDrawListPtr dl)
    {
        var closePos = new Vector2(origin.X + dx, origin.Y + dy);
        ImGui.SetCursorScreenPos(closePos);
        ImGui.InvisibleButton($"##remove_{spawnKey}", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked();

        var col = hovered ? 0xFFFFFFFF : 0x66888888u;
        var cc  = closePos + new Vector2(size / 2f);
        var d   = S(3f);
        dl.AddLine(cc + new Vector2(-d, -d), cc + new Vector2(d,  d), col, 1.5f);
        dl.AddLine(cc + new Vector2(-d,  d), cc + new Vector2(d, -d), col, 1.5f);

        if (hovered)
        {
            using (ImRaii.Tooltip())
                ImGui.TextUnformatted("Remove this spawn");
        }
        return clicked;
    }

    // Small "{}" button — clicked, copies this spawn's full raw Faloop JSON.
    private static bool DrawRawButton(Vector2 origin, float dx, float dy,
                                      long spawnKey, ImDrawListPtr dl)
    {
        var size = S(16f);
        var p = new Vector2(origin.X + dx, origin.Y + dy);
        ImGui.SetCursorScreenPos(p);
        ImGui.InvisibleButton($"##raw_{spawnKey}", new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked();

        var col = hovered ? 0xFFFFFFFFu : 0x70AAAAAAu;
        var ts  = Measure(_fMedium, PxMedium, "{}");
        DrawText(dl, _fMedium, PxMedium, p + (new Vector2(size, size) - ts) * 0.5f, col, "{}");

        if (hovered)
            using (ImRaii.Tooltip())
                ImGui.TextUnformatted("Copy this spawn's raw Faloop JSON");
        return clicked;
    }

    // Copy the raw event JSON to the clipboard and mirror it to the log, so a
    // spawn's exact Faloop payload can be inspected (e.g. for pre-release).
    private static void OutputRawJson(SpawnInfo spawn)
    {
        var json = string.IsNullOrEmpty(spawn.RawEvent)
            ? "(no raw event captured for this spawn)"
            : spawn.RawEvent;
        try { ImGui.SetClipboardText(json); }
        catch (Exception ex) { Plugin.Log.Warning($"[Faloop] Clipboard copy failed: {ex.Message}"); }

        Plugin.Log.Information($"[Faloop] Raw JSON — {spawn.MobName} on {spawn.World}:\n{json}");
        Plugin.ChatGui.Print(
            $"[FaloopMessenger] Raw JSON for {spawn.MobName} copied to clipboard " +
            $"({json.Length} chars). Full text is also in the plugin log.");
    }

    private static void DrawRankBadge(Vector2 center, float radius, string label,
                                      uint rankU32, bool fresh, ImDrawListPtr dl,
                                      bool useTitleFont)
    {
        if (fresh)
        {
            var pulse = 0.5f + 0.5f * MathF.Sin((float)(Environment.TickCount64 / 220.0));
            var glowR = radius + S(4f) + S(3f) * pulse;
            var glowA = (uint)(0x40 + 0x40 * pulse) << 24 | (rankU32 & 0x00FFFFFF);
            dl.AddCircleFilled(center, glowR, glowA);
        }
        dl.AddCircle(center, radius, rankU32, 0, 2.5f);

        // "SS" needs the smaller font to fit the circle even on the big badge.
        var big  = useTitleFont && label.Length < 2;
        var font = big ? _fTitle : _fMedium;
        var px   = big ? PxTitle : PxMedium;
        var ts   = Measure(font, px, label);
        DrawText(dl, font, px, center - ts * 0.5f, rankU32, label);
    }

    // Small high-contrast "i2" pill next to the world title. Manages its own
    // (medium) font so it stays small even when the caller pushed FontTitle.
    // Returns the pill's right-edge X.
    private static Vector2 InstancePad => new(S(7f), S(2f));

    private static float MeasureInstanceBadge(int instance)
        => Measure(_fMedium, PxMedium, $"i{instance}").X + InstancePad.X * 2f;

    private static float DrawInstanceBadge(ImDrawListPtr dl, Vector2 topLeft, int instance)
    {
        var label = $"i{instance}";
        var ts    = Measure(_fMedium, PxMedium, label);
        var max   = topLeft + ts + InstancePad * 2f;
        dl.AddRectFilled(topLeft, max, ImGui.GetColorU32(Theme.InstanceBg), S(4f));
        DrawText(dl, _fMedium, PxMedium, topLeft + InstancePad,
            ImGui.GetColorU32(Theme.InstanceText), label);
        return max.X;
    }

    // Draw text, ellipsizing only if it can't fit maxW (last-resort safety;
    // the fluid layout means this almost never triggers). When it DOES
    // truncate, hovering the text shows the full string in a tooltip so it's
    // never lost. Returns the X the text actually ended at, so a trailing
    // element (the instance badge) can butt right up against it.
    private static float DrawClipped(ImDrawListPtr dl, ImFontPtr font, float px,
                                     Vector2 pos, uint col, string s, float maxW)
    {
        if (string.IsNullOrEmpty(s)) return pos.X;
        if (maxW <= 1f) return pos.X;
        if (Measure(font, px, s).X <= maxW)
        {
            DrawText(dl, font, px, pos, col, s);
            return pos.X + Measure(font, px, s).X;
        }

        var full = s;
        const string ell = "…";
        while (s.Length > 1 && Measure(font, px, s + ell).X > maxW)
            s = s[..^1];
        s += ell;
        DrawText(dl, font, px, pos, col, s);

        var w = Measure(font, px, s).X;
        if (ImGui.IsWindowHovered() &&
            ImGui.IsMouseHoveringRect(pos, new Vector2(pos.X + w, pos.Y + px)))
        {
            using (ImRaii.Tooltip())
                ImGui.TextUnformatted(full);
        }
        return pos.X + w;
    }

    // Route hint, fixed slot, clipped to maxW. Blank if no route data (caller
    // leaves the slot empty rather than reflowing the card).
    private static void DrawRouteHintClipped(SpawnInfo spawn, Vector2 pos,
                                             ImDrawListPtr dl, float maxW)
    {
        if (spawn.ZonePoiId <= 0) return;
        if (!FaloopRoutes.RouteByPoiId.TryGetValue(spawn.ZonePoiId, out var route)) return;

        var line = string.IsNullOrEmpty(route.Hint)
            ? $"→ {route.Aetheryte}"
            : $"→ {route.Aetheryte}  ·  {route.Hint}";
        DrawClipped(dl, _fMedium, PxMedium, pos, ImGui.GetColorU32(Theme.RouteHint), line, maxW);
    }

    // ── Pull state ────────────────────────────────────────────────────

    private readonly struct PullInfo
    {
        public bool   Enabled  { get; init; }
        public bool   Ready    { get; init; }
        public string Et       { get; init; }   // Eorzean clock "HH:MM" the pull is due
        public string Countdown{ get; init; }   // real-time "m:ss" (tooltip)
        public float  Frac     { get; init; }   // 1→0 drain (countdown remaining)
    }

    private static PullInfo PullState(SpawnInfo spawn)
    {
        var mins = Plugin.Config.PullTimerMinutes;
        if (mins <= 0) return default;   // Enabled = false

        var total     = TimeSpan.FromMinutes(mins);
        var remaining = total - (TimeSync.ServerNow - spawn.ReportedAt);

        if (remaining <= TimeSpan.Zero)
            return new PullInfo { Enabled = true, Ready = true, Frac = 0f };

        return new PullInfo
        {
            Enabled   = true,
            Ready     = false,
            Et        = EorzeaClock(DateTime.UtcNow + remaining),
            Countdown = MmSs(remaining),
            Frac      = MathF.Min(1f, (float)(remaining.TotalSeconds / total.TotalSeconds)),
        };
    }

    // Minimal timer chip — small, low-chrome, but noticeable via colour and a
    // gentle pulse in the final stretch / when ready. Shows the Eorzean clock
    // time the pull is due ("ET HH:MM"), since that's the value you watch the
    // in-game clock for; the real-time m:ss countdown is in the hover tooltip.
    private static void DrawTimerChip(ImDrawListPtr dl, Vector2 pos, Vector2 size,
                                      PullInfo p, long spawnKey)
    {
        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton($"##timer_{spawnKey}", size);
        var hovered = ImGui.IsItemHovered();

        var accent = p.Ready ? Theme.PullReady : Theme.PullWait;

        // Pulse: while ready, or in the last ~18% of the countdown.
        var imminent = p.Ready || p.Frac < 0.18f;
        var pulse    = imminent
            ? 0.65f + 0.35f * (0.5f + 0.5f * MathF.Sin((float)(Environment.TickCount64 / 180.0)))
            : 1f;

        var max = pos + size;
        if (p.Ready)
        {
            var fill = new Vector4(accent.X, accent.Y, accent.Z, pulse);
            dl.AddRectFilled(pos, max, ImGui.GetColorU32(fill), S(5f));
        }
        else
        {
            dl.AddRectFilled(pos, max, ImGui.GetColorU32(Theme.PullPanelBg), S(5f));
            var border = new Vector4(accent.X, accent.Y, accent.Z, pulse);
            dl.AddRect(pos, max, ImGui.GetColorU32(border), S(5f), 0, 1.3f);
        }

        {
            var label = p.Ready ? "PULL" : $"ET {p.Et}";
            var ts    = Measure(_fMedium, PxMedium, label);
            var tcol  = p.Ready
                ? Theme.PullPanelBg
                : new Vector4(accent.X, accent.Y, accent.Z, pulse);
            DrawText(dl, _fMedium, PxMedium,
                new Vector2(pos.X + (size.X - ts.X) / 2f, pos.Y + (size.Y - ts.Y) / 2f),
                ImGui.GetColorU32(tcol), label);
        }

        if (hovered)
            using (ImRaii.Tooltip())
                ImGui.TextUnformatted(p.Ready
                    ? "Pull now"
                    : $"Pull in {p.Countdown}  ·  ET {p.Et}");
    }


    // ── Meta strip ────────────────────────────────────────────────────

    // zone · ●age · (x, y) · by reporter — one tertiary line, with a filled/
    // hollow freshness dot (non-colour cue) and the age in fresh-cyan when
    // recent. Returns the Y for the next row. Segments past `rightX` are
    // dropped so the strip never collides with the thumbnail.
    private static float DrawMetaRow(ImDrawListPtr dl, SpawnInfo spawn, bool fresh,
                                     Vector2 pos, float rightX, bool compact = false)
    {
        var tertiary = ImGui.GetColorU32(Theme.TextTertiary);
        var sepU     = ImGui.GetColorU32(Theme.Muted);
        var x        = pos.X;
        var midY     = pos.Y + PxMedium / 2f;

        void Sep()
        {
            if (x >= rightX) return;
            var w = Measure(_fMedium, PxMedium, "  ·  ").X;
            if (x + w > rightX) { x = rightX; return; }   // no room → stop the strip
            DrawText(dl, _fMedium, PxMedium, new Vector2(x, pos.Y), sepU, "  ·  ");
            x += w;
        }
        void Seg(string s, uint col)
        {
            if (string.IsNullOrEmpty(s) || x >= rightX) return;
            // Clip the *active* segment too, so a long leading zone name
            // truncates instead of overrunning into the map.
            x = DrawClipped(dl, _fMedium, PxMedium, new Vector2(x, pos.Y), col, s, rightX - x);
        }

        // Pre-release (Faloop "isScheduled"): only scheduled reports show this.
        // Count down to the public-release moment (ReportedAt + scheduleDelay
        // seconds) as "-Ns", then flip to RELEASED so the flag never goes
        // stale. Other users without the permission just don't get these.
        if (spawn.IsScheduled)
        {
            // Three sub-forms of "not yet public" — distinct labels so the
            // user can tell at a glance whether the public will see this
            // soon (timed), eventually (untimed pre-release), or only when
            // the reporter manually pushes it (early-access / stage:null).
            // None of these mean "go pull it" — the green RELEASED badge
            // only fires from the OnNewSpawn re-trigger when an actual
            // non-scheduled event arrives (see FaloopSocketClient upsert).
            if (spawn.Stage == null)
            {
                // Manual release: privileged tier sees it, public does NOT.
                // No timer is coming; we'll only know it went public when
                // Faloop pushes the follow-up isScheduled:false event.
                Seg("EARLY ACCESS", ImGui.GetColorU32(Theme.RouteHint));
            }
            else if (spawn.ScheduleDelay.HasValue && spawn.ScheduleDelay.Value > 0)
            {
                var until = spawn.ReportedAt
                          + TimeSpan.FromSeconds(spawn.ScheduleDelay.Value)
                          - TimeSync.ServerNow;
                if (until > TimeSpan.Zero)
                    Seg($"PRE-RELEASE -{FormatAge(until)}", ImGui.GetColorU32(Theme.Warn));
                else
                    // Timer expired locally but we haven't received the
                    // public event yet — don't lie and say RELEASED.
                    Seg("PRE-RELEASE (pending)", ImGui.GetColorU32(Theme.Warn));
            }
            else
            {
                // Pre-release without a timer (rare, but Faloop emits it).
                Seg("PRE-RELEASE", ImGui.GetColorU32(Theme.Warn));
            }
            Sep();
        }

        Seg(spawn.ZoneName, tertiary);
        Sep();

        // Freshness dot: filled = fresh, hollow ring = stale.
        if (x <= rightX)
        {
            var c = new Vector2(x + S(4f), midY);
            if (fresh) dl.AddCircleFilled(c, S(4f), ImGui.GetColorU32(Theme.AgeFresh));
            else       dl.AddCircle(c, S(4f), tertiary, 0, 1.4f);
            x += S(13f);
        }
        Seg(FormatAge(TimeSync.ServerNow - spawn.ReportedAt),
            fresh ? ImGui.GetColorU32(Theme.AgeFresh) : tertiary);

        if (!compact && spawn.X > 0 && spawn.Y > 0)
        {
            Sep();
            Seg($"({spawn.X:F1}, {spawn.Y:F1})", tertiary);
        }
        if (!string.IsNullOrEmpty(spawn.Reporter))
        {
            Sep();
            Seg($"by {spawn.Reporter}", tertiary);
        }

        return pos.Y + PxMedium + S(4f);
    }

    private static string MmSs(TimeSpan t)
    {
        var total = (int)t.TotalSeconds;
        return $"{total / 60}:{total % 60:D2}";
    }

    // ── Action buttons ────────────────────────────────────────────────

    // One accent PRIMARY (Teleport) on top + neutral secondaries stacked below
    // — keeps the card short (no extra button row) while still giving the eye
    // a single primary instead of a wall of identical gold chips.
    private static void DrawActionButtonsStacked(ImDrawListPtr dl, SpawnInfo spawn,
                                                 float x0, float y0,
                                                 float btnW, float btnH, float gap, long spawnKey)
    {
        var canAct = spawn.TerritoryId > 0;
        var isTP   = TeleportRoutine.InProgress.Contains(spawnKey);
        var sz = new Vector2(btnW, btnH);
        var y  = y0;

        DrawButton(dl, isTP ? "TP'ing…" : "Teleport", $"##tp_{spawnKey}",
            new Vector2(x0, y), sz, primary: true, disabled: isTP || !canAct,
            () => TeleportRoutine.Teleport(spawn));
        y += btnH + gap;

        // Flag removed — clicking the map thumbnail places the flag now.
        DrawButton(dl, "Ping", $"##ping_{spawnKey}", new Vector2(x0, y), sz,
            primary: false, disabled: !canAct, () => TeleportRoutine.Ping(spawn));
        y += btnH + gap;
        DrawButton(dl, "PF", $"##pf_{spawnKey}", new Vector2(x0, y), sz,
            primary: false, disabled: false, TeleportRoutine.OpenPartyFinder);
    }

    private static void DrawActionButtonsCompact(ImDrawListPtr dl, SpawnInfo spawn,
                                                 float x0, float y0, long spawnKey)
    {
        var canAct = spawn.TerritoryId > 0;
        var isTP   = TeleportRoutine.InProgress.Contains(spawnKey);
        var sz = new Vector2(CmpButtonW, CmpBtnH);
        var x  = x0;

        DrawButton(dl, isTP ? "TP…" : "TP", $"##tp_{spawnKey}", new Vector2(x, y0), sz,
            primary: true, disabled: isTP || !canAct, () => TeleportRoutine.Teleport(spawn));
        x += CmpButtonW + CmpBtnGap;

        // Compact has no map thumbnail to click, so Flag lives here.
        DrawButton(dl, "Flag", $"##flag_{spawnKey}", new Vector2(x, y0), sz,
            primary: false, disabled: !canAct, () => TeleportRoutine.SetFlag(spawn));
        x += CmpButtonW + CmpBtnGap;
        DrawButton(dl, "PF", $"##pf_{spawnKey}", new Vector2(x, y0), sz,
            primary: false, disabled: false, TeleportRoutine.OpenPartyFinder);
    }

    // Custom-drawn button — the label renders at a fixed px (via DrawText), so
    // a large Dalamud font can't clip or reflow it. InvisibleButton powers the
    // click/hover; the rect + label are drawn on the window draw list.
    private static void DrawButton(ImDrawListPtr dl, string label, string id,
                                   Vector2 pos, Vector2 size, bool primary,
                                   bool disabled, System.Action onClick)
    {
        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton(id, size);
        var hovered = !disabled && ImGui.IsItemHovered();
        var active  = !disabled && ImGui.IsItemActive();
        if (!disabled && ImGui.IsItemClicked()) onClick();

        var bg = primary
            ? (active ? Theme.BtnGoldActv : hovered ? Theme.BtnGoldHov : Theme.BtnGold)
            : (active ? Theme.BtnNeutralActv : hovered ? Theme.BtnNeutralHov : Theme.BtnNeutral);
        var txt = primary ? Theme.BtnGoldText : Theme.BtnNeutralText;
        if (disabled) { bg.W *= 0.4f; txt.W *= 0.55f; }

        dl.AddRectFilled(pos, pos + size, ImGui.GetColorU32(bg), S(4f));
        var ts = Measure(_fMedium, PxMedium, label);
        DrawText(dl, _fMedium, PxMedium, pos + (size - ts) * 0.5f,
            ImGui.GetColorU32(txt), label);
    }

    // ── Map thumbnail ────────────────────────────────────────────────

    private static void DrawMapThumb(SpawnInfo spawn, Vector2 pos, float size)
    {
        var dl   = ImGui.GetWindowDrawList();
        var path = TryGetMapTexturePath(spawn.MapId);

        if (path == null)
        {
            dl.AddRectFilled(pos, pos + new Vector2(size, size),
                ImGui.GetColorU32(new Vector4(0.10f, 0.10f, 0.12f, 1f)), 4f);
            return;
        }

        var sharedTex = Plugin.TextureProvider.GetFromGame(path);
        var wrap      = sharedTex.GetWrapOrEmpty();

        // All reported points (SS minions report several at once). Fall back
        // to the single RawX/RawY when Points is empty.
        var pts = spawn.Points.Count > 0
            ? spawn.Points
            : (spawn.RawX > 0 && spawn.RawY > 0
                ? new List<SpawnPoint> { new(spawn.RawX, spawn.RawY, spawn.X, spawn.Y) }
                : new List<SpawnPoint>());
        var single    = pts.Count <= 1;
        var hasCoords = pts.Count > 0;

        Vector2 uvMin = Vector2.Zero, uvMax = Vector2.One;
        if (hasCoords && single)
        {
            // Single point → zoom in. Multiple → show the whole map so every
            // marker is visible (a POI cloud is spread across the zone).
            var uC   = pts[0].RawX / 2048f;
            var vC   = pts[0].RawY / 2048f;
            var half = ThumbZoom / 2f;
            var uMin = MathF.Max(0f, MathF.Min(1f - ThumbZoom, uC - half));
            var vMin = MathF.Max(0f, MathF.Min(1f - ThumbZoom, vC - half));
            uvMin = new Vector2(uMin, vMin);
            uvMax = uvMin + new Vector2(ThumbZoom);
        }

        var thumbMin     = pos;
        var thumbMax     = pos + new Vector2(size, size);
        var thumbHovered = ImGui.IsMouseHoveringRect(thumbMin, thumbMax) && ImGui.IsWindowHovered();
        if (thumbHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && spawn.TerritoryId > 0)
            TeleportRoutine.SetFlag(spawn);

        dl.AddImageRounded(wrap.Handle, pos, pos + new Vector2(size, size),
            uvMin, uvMax, 0xFFFFFFFF, S(4f));

        if (thumbHovered)
        {
            dl.AddRect(pos, pos + new Vector2(size, size), 0xFF40D9FF, S(4f), 0, 1.5f);
            if (spawn.TerritoryId > 0)
                using (ImRaii.Tooltip())
                    ImGui.TextUnformatted("Click to place a map flag");
        }

        if (hasCoords)
        {
            Vector2 ToScreen(SpawnPoint p)
            {
                var u = (p.RawX / 2048f - uvMin.X) / (uvMax.X - uvMin.X);
                var v = (p.RawY / 2048f - uvMin.Y) / (uvMax.Y - uvMin.Y);
                return pos + new Vector2(u, v) * size;
            }

            // Clean pulsing dot, scalable so a cloud of minion points stays
            // legible (smaller dots when there are many).
            var pulse = 0.5f + 0.5f * MathF.Sin((float)(Environment.TickCount64 / 240.0));
            void Dot(Vector2 m, float k)
            {
                var haloR = (S(11f) + S(3.5f) * pulse) * k;
                var haloA = (byte)(0x30 + 0x40 * (1f - pulse));
                dl.AddCircleFilled(m, haloR, ((uint)haloA << 24) | 0x0040DAFFu);
                dl.AddCircleFilled(m + new Vector2(0.5f, 1f), S(6.5f) * k, 0x55000000);
                dl.AddCircleFilled(m, S(6f) * k, 0xFF40DAFFu);
                dl.AddCircle      (m, S(6f) * k, 0xC0202830u, 0, 1.5f);
                dl.AddCircleFilled(m, S(2f) * k, 0xFFFFFFFFu);
            }

            if (single)
            {
                var m = ToScreen(pts[0]);
                // Route hints only make sense for a single target.
                DrawCrossZoneArrow(dl, spawn, m, pos, size, uvMin, uvMax);
                DrawAetheryteMarker(dl, spawn, m, pos, size, uvMin, uvMax);
                Dot(m, 1f);
            }
            else
            {
                // Every reported SS-minion point gets its own marker.
                foreach (var p in pts)
                    Dot(ToScreen(p), 0.7f);
            }
        }
    }

    // If this spawn's route requires walking in from a neighbouring zone's
    // boundary, draw a cyan arrow from the boundary's location to the spawn
    // marker. When the boundary falls outside the thumbnail's zoomed crop,
    // the arrow starts at the nearest edge of the thumbnail (and the rest of
    // the line is "off-screen", implying "from that direction").
    private static void DrawCrossZoneArrow(ImDrawListPtr dl, SpawnInfo spawn,
                                           Vector2 spawnPos, Vector2 thumbPos, float thumbSize,
                                           Vector2 uvMin, Vector2 uvMax)
    {
        if (spawn.ZonePoiId <= 0) return;
        if (!FaloopRoutes.RouteByPoiId.TryGetValue(spawn.ZonePoiId, out var route)) return;
        if (route.GatewayX <= 0 || route.GatewayY <= 0) return;   // in-zone aetheryte → no arrow

        // Gateway position in display coords (might be off the visible crop)
        var gU = route.GatewayX / 2048f;
        var gV = route.GatewayY / 2048f;
        var gDisplayU = (gU - uvMin.X) / (uvMax.X - uvMin.X);
        var gDisplayV = (gV - uvMin.Y) / (uvMax.Y - uvMin.Y);
        var gatewayRaw = thumbPos + new Vector2(gDisplayU, gDisplayV) * thumbSize;

        var thumbMin = thumbPos;
        var thumbMax = thumbPos + new Vector2(thumbSize, thumbSize);

        var gatewayVisible = gatewayRaw.X >= thumbMin.X && gatewayRaw.X <= thumbMax.X &&
                             gatewayRaw.Y >= thumbMin.Y && gatewayRaw.Y <= thumbMax.Y;

        // Clamp line start to the thumbnail boundary if gateway is off-screen
        var lineStart = gatewayVisible
            ? gatewayRaw
            : ClampLineToRect(spawnPos, gatewayRaw, thumbMin, thumbMax);

        var rawDir = spawnPos - lineStart;
        if (rawDir.LengthSquared() < 16f) return;   // too close to bother

        var dir  = Vector2.Normalize(rawDir);
        var perp = new Vector2(-dir.Y, dir.X);

        // Arrowhead positioned just shy of the spawn marker
        var tipPoint   = spawnPos - dir * S(10f);
        var arrowBase  = tipPoint - dir * S(6.5f);
        var arrowLeft  = arrowBase + perp * S(4.2f);
        var arrowRight = arrowBase - perp * S(4.2f);

        const uint LineColor   = 0xFFFFEC52;        // light cyan (ABGR)
        const uint OutlineColor = 0xCC181E28;
        const uint ShadowColor = 0x60000000;

        // Line (shadow + main)
        dl.AddLine(lineStart + new Vector2(1f, 1.5f),
                   arrowBase + new Vector2(1f, 1.5f), ShadowColor, 3.5f);
        dl.AddLine(lineStart, arrowBase, LineColor, 2.5f);

        // Arrowhead (shadow + fill + outline)
        dl.AddTriangleFilled(tipPoint + new Vector2(1f, 1.5f),
                             arrowLeft + new Vector2(1f, 1.5f),
                             arrowRight + new Vector2(1f, 1.5f), ShadowColor);
        dl.AddTriangleFilled(tipPoint, arrowLeft, arrowRight, LineColor);
        dl.AddTriangle      (tipPoint, arrowLeft, arrowRight, OutlineColor, 1.2f);

        // Small entry marker at the gateway end if it's visible in the crop
        if (gatewayVisible)
        {
            dl.AddCircleFilled(gatewayRaw, S(3.5f), LineColor);
            dl.AddCircle      (gatewayRaw, S(3.5f), OutlineColor, 0, 1f);
        }
    }

    // Walks the line from `inside` (which must be inside the rect) toward
    // `outside`, returning the point where the line crosses the rect edge.
    // If `outside` is already inside the rect, returns it unchanged.
    private static Vector2 ClampLineToRect(Vector2 inside, Vector2 outside,
                                           Vector2 rectMin, Vector2 rectMax)
    {
        if (outside.X >= rectMin.X && outside.X <= rectMax.X &&
            outside.Y >= rectMin.Y && outside.Y <= rectMax.Y)
            return outside;

        var dir = outside - inside;
        var t   = 1f;
        if (dir.X >  0.0001f) t = MathF.Min(t, (rectMax.X - inside.X) / dir.X);
        if (dir.X < -0.0001f) t = MathF.Min(t, (rectMin.X - inside.X) / dir.X);
        if (dir.Y >  0.0001f) t = MathF.Min(t, (rectMax.Y - inside.Y) / dir.Y);
        if (dir.Y < -0.0001f) t = MathF.Min(t, (rectMin.Y - inside.Y) / dir.Y);
        return inside + dir * t;
    }

    // Draw the in-zone aetheryte you'd teleport to as a small blue crystal
    // glyph, plus a faint connector to the spawn marker so the thumbnail reads
    // "TP here → run to mob". Cross-zone routes already have their own gateway
    // arrow (ResolveInZoneAetheryte returns null for those), so the two never
    // fight. Aetheryte sky-blue is deliberately distinct from the gold spawn
    // dot and the cyan cross-zone arrow.
    private static void DrawAetheryteMarker(ImDrawListPtr dl, SpawnInfo spawn,
                                            Vector2 spawnPos, Vector2 thumbPos, float thumbSize,
                                            Vector2 uvMin, Vector2 uvMax)
    {
        var raw = ResolveInZoneAetheryte(spawn);
        if (raw == null) return;

        var aU = (raw.Value.x / 2048f - uvMin.X) / (uvMax.X - uvMin.X);
        var aV = (raw.Value.y / 2048f - uvMin.Y) / (uvMax.Y - uvMin.Y);
        var a  = thumbPos + new Vector2(aU, aV) * thumbSize;

        // Off the visible crop → skip (its zone is on-map but cropped out).
        if (a.X < thumbPos.X || a.X > thumbPos.X + thumbSize ||
            a.Y < thumbPos.Y || a.Y > thumbPos.Y + thumbSize)
            return;

        const uint Blue    = 0xFFFFB45A;   // ABGR — sky blue
        const uint Outline  = 0xCC181E28;
        const uint Shadow   = 0x60000000;

        // Faint connector aetheryte → spawn (skip if they're basically on top).
        if ((spawnPos - a).LengthSquared() > 36f)
        {
            dl.AddLine(a + new Vector2(1f, 1.5f), spawnPos + new Vector2(1f, 1.5f), Shadow, 2.5f);
            dl.AddLine(a, spawnPos, (Blue & 0x00FFFFFF) | 0x70000000u, 1.8f);
        }

        // Diamond crystal built from two triangles (known-good binding API).
        void Diamond(Vector2 c, float r, uint col)
        {
            var n = c + new Vector2(0, -r);
            var e = c + new Vector2(r, 0);
            var s = c + new Vector2(0,  r);
            var w = c + new Vector2(-r, 0);
            dl.AddTriangleFilled(n, e, s, col);
            dl.AddTriangleFilled(n, s, w, col);
        }

        Diamond(a + new Vector2(0.5f, 1f), S(5.5f), Shadow);
        Diamond(a, S(5f), Blue);

        var an  = a + new Vector2(0, -S(5f));
        var ae  = a + new Vector2(S(5f), 0);
        var as_ = a + new Vector2(0, S(5f));
        var aw  = a + new Vector2(-S(5f), 0);
        dl.AddLine(an, ae, Outline, 1.2f);
        dl.AddLine(ae, as_, Outline, 1.2f);
        dl.AddLine(as_, aw, Outline, 1.2f);
        dl.AddLine(aw, an, Outline, 1.2f);

        Diamond(a, S(1.8f), 0xFFFFFFFFu);
    }

    // Resolve the raw 2048-scale coords of the in-zone aetheryte this spawn's
    // route teleports you to. Returns null for cross-zone routes (handled by
    // the gateway arrow instead) or when no aetheryte position is known.
    private static (int x, int y)? ResolveInZoneAetheryte(SpawnInfo spawn)
    {
        if (spawn.TerritoryId == 0) return null;

        string? aetheryteName = null;
        if (spawn.ZonePoiId > 0 &&
            FaloopRoutes.RouteByPoiId.TryGetValue(spawn.ZonePoiId, out var route))
        {
            // Cross-zone route — the gateway arrow already shows the entry.
            if (route.GatewayX > 0 || route.GatewayY > 0) return null;
            aetheryteName = route.Aetheryte;
        }

        aetheryteName ??= TeleportRoutine.FindAetheryteForTerritory(
            spawn.TerritoryId, spawn.RawX, spawn.RawY);
        if (string.IsNullOrEmpty(aetheryteName)) return null;

        var slug = FaloopData.SlugForTerritory(spawn.TerritoryId);
        if (slug == null ||
            !FaloopData.ZoneAetherytes.TryGetValue(slug, out var aetherytes))
            return null;

        foreach (var (name, x, y) in aetherytes)
            if (string.Equals(name, aetheryteName, StringComparison.OrdinalIgnoreCase))
                return (x, y);
        return null;
    }

    private static string? TryGetMapTexturePath(uint mapId)
    {
        if (mapId == 0) return null;
        var sheet = Plugin.DataManager.GetExcelSheet<Map>();
        if (sheet == null || !sheet.TryGetRow(mapId, out var map)) return null;
        var key = map.Id.ToString();
        if (string.IsNullOrEmpty(key)) return null;
        var fileName = key.Replace("/", string.Empty);
        return $"ui/map/{key}/{fileName}_m.tex";
    }

    // Zone name plus the FFXIV instance suffix (" i2") when the spawn is in an
    // instanced copy of the zone — critical for actually finding the mob, since
    // instance 1/2/3 are separate map copies. Shared by every card variant and
    // the chat echo so the format never drifts.
    internal static string ZoneLabel(SpawnInfo spawn) =>
        spawn.ZoneInstance > 0
            ? $"{spawn.ZoneName}  i{spawn.ZoneInstance}"
            : spawn.ZoneName;

    private static string FormatAge(TimeSpan age) =>
        age.TotalHours >= 1
            ? $"{(int)age.TotalHours}h{age.Minutes:D2}m"
            : age.TotalMinutes >= 1
                ? $"{(int)age.TotalMinutes}m{age.Seconds:D2}s"
                : $"{age.Seconds}s";

    // Convert a real-world UTC instant to FFXIV Eorzean clock "HH:MM".
    // 1 Eorzean hour = 175 real seconds → Eorzean seconds = unix * 3600/175.
    private static string EorzeaClock(DateTime utc)
    {
        var unix = (long)(utc - DateTime.UnixEpoch).TotalSeconds;
        var et   = (long)(unix * (3600.0 / 175.0));
        var h    = (et / 3600) % 24;
        var m    = (et / 60)   % 60;
        return $"{h:D2}:{m:D2}";
    }
}
