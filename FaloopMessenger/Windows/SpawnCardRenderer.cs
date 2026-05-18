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
    // Redesigned card: world is the title, the pull timer gets a dedicated
    // right-side panel, actions sit on their own bottom row with one primary.
    private const float StdCardHeight   = 150f;
    private const float StdCardPad      = 12f;
    private const float StdStripeWidth  = 4f;
    private const float StdBadgeRadius  = 18f;
    private const float StdThumbSize    = 78f;
    private const float StdPullW        = 148f;
    private const float StdPullH        = 56f;
    private const float StdPrimaryW     = 104f;
    private const float StdSecondaryW   = 58f;
    private const float StdBtnH         = 26f;
    private const float StdBtnGap       = 6f;

    private const float CmpCardHeight   = 70f;
    private const float CmpCardPad      = 7f;
    private const float CmpStripeWidth  = 3f;
    private const float CmpBadgeRadius  = 13f;
    private const float CmpButtonW      = 56f;
    private const float CmpBtnH         = 22f;
    private const float CmpBtnGap       = 4f;

    private const float CardRound       = 6f;
    private const float ThumbZoom       = 0.25f;   // fraction of texture to show
    private const float FadeMs          = 600f;    // intro flash duration

    // ── Public entry ──────────────────────────────────────────────────

    public static void DrawCard(SpawnInfo spawn, FaloopSocketClient client, bool compact)
    {
        if (compact) DrawCompactCard(spawn, client);
        else         DrawStandardCard(spawn, client);
    }

    public static void DrawDeadRow(SpawnInfo spawn)
    {
        const float DeadH = 30f;
        var origin = ImGui.GetCursorScreenPos();
        var width  = ImGui.GetContentRegionAvail().X;
        var dl     = ImGui.GetWindowDrawList();

        dl.AddRectFilled(origin, origin + new Vector2(width, DeadH),
            ImGui.GetColorU32(new Vector4(0.10f, 0.11f, 0.13f, 0.5f)), CardRound);
        dl.AddRectFilled(origin, origin + new Vector2(StdStripeWidth, DeadH),
            ImGui.GetColorU32(Theme.Muted), CardRound);

        var badgeC = new Vector2(origin.X + StdStripeWidth + 14f, origin.Y + DeadH / 2f);
        var mutedU = ImGui.GetColorU32(Theme.Muted);
        dl.AddCircle(badgeC, 8f, mutedU, 0, 1.5f);
        var xs = ImGui.CalcTextSize("✗");
        dl.AddText(badgeC - xs * 0.5f, mutedU, "✗");

        var killedAt = spawn.KilledAt ?? TimeSync.ServerNow;
        var age      = TimeSync.ServerNow - killedAt;
        // World-first, mirroring the live card's hierarchy.
        var parts    = new List<string> { spawn.World, spawn.MobName, ZoneLabel(spawn) };
        if (!string.IsNullOrEmpty(spawn.Reporter)) parts.Add($"by {spawn.Reporter}");
        parts.Add($"killed {FormatAge(age)} ago");
        var text = string.Join("  ·  ", parts);

        var textY = origin.Y + (DeadH - ImGui.GetTextLineHeight()) / 2f;
        dl.AddText(new Vector2(badgeC.X + 14f, textY),
            ImGui.GetColorU32(Theme.TextTertiary), text);

        ImGui.Dummy(new Vector2(width, DeadH));
        ImGui.Spacing();
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
            if (DrawCloseButton(origin, StdStripeWidth + 4f, 5f, 14f, spawnKey, dl))
            {
                client.RemoveSpawn(spawn);
                _firstRenderAt.Remove(spawnKey);
                TeleportRoutine.InProgress.Remove(spawnKey);
                return;
            }

            // Rank stays as accent identity ONLY (stripe + badge). Everything
            // else uses neutral/semantic colour so the accent isn't diluted.
            var badgeCenter = new Vector2(
                origin.X + StdStripeWidth + 10f + StdBadgeRadius,
                origin.Y + 40f);
            DrawRankBadge(badgeCenter, StdBadgeRadius, spawn.Rank, rankU32, fresh, dl, useTitleFont: true);

            // ── Right cluster: PULL panel + thumbnail ──────────────────
            var pull   = PullState(spawn);
            var pullX  = origin.X + width - StdCardPad - StdPullW;
            var pullY  = origin.Y + StdCardPad;
            var hasPull = pull.Enabled;
            if (hasPull)
                DrawPullPanel(dl, new Vector2(pullX, pullY), pull);

            var thumbRightEdge = hasPull ? pullX - 12f : origin.X + width - StdCardPad;
            var thumbX = thumbRightEdge - StdThumbSize;
            var thumbY = origin.Y + StdCardPad;
            DrawMapThumb(spawn, new Vector2(thumbX, thumbY), StdThumbSize);

            // ── Text block (fixed rows → columns align down a list) ────
            var textX     = origin.X + StdStripeWidth + 12f + StdBadgeRadius * 2f + 18f;
            var textRight = thumbX - 14f;
            var y         = origin.Y + StdCardPad;

            // Row 1 — WORLD (the title) + instance badge
            using (Plugin.FontTitle.Push())
            {
                dl.AddText(new Vector2(textX, y),
                    ImGui.GetColorU32(Theme.TextPrimary), spawn.World);
                var ww = ImGui.CalcTextSize(spawn.World).X;
                if (spawn.ZoneInstance > 0)
                    DrawInstanceBadge(dl,
                        new Vector2(textX + ww + 10f, y + 2f), spawn.ZoneInstance);
                y += ImGui.GetTextLineHeight() + 2f;
            }

            // Row 2 — mob name (secondary)
            using (Plugin.FontMedium.Push())
            {
                dl.AddText(new Vector2(textX, y),
                    ImGui.GetColorU32(Theme.TextSecondary), spawn.MobName);
                y += ImGui.GetTextLineHeight() + 5f;
            }

            // Row 3 — meta strip: zone · ●age · coords · by reporter
            y = DrawMetaRow(dl, spawn, fresh, new Vector2(textX, y), textRight);

            // Row 4 — route hint (fixed slot; blank if none, never reflows)
            DrawRouteHint(spawn, new Vector2(textX, y), dl);

            // ── Bottom action row: one primary + neutral secondaries ───
            var btnY = origin.Y + StdCardHeight - StdCardPad - StdBtnH;
            DrawActionButtons(spawn, textX, btnY, spawnKey);
        }

        ImGui.SetCursorScreenPos(origin + new Vector2(0f, StdCardHeight + 6f));
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

            if (DrawCloseButton(origin, CmpStripeWidth + 3f, 3f, 12f, spawnKey, dl))
            {
                client.RemoveSpawn(spawn);
                _firstRenderAt.Remove(spawnKey);
                TeleportRoutine.InProgress.Remove(spawnKey);
                return;
            }

            var badgeCenter = new Vector2(
                origin.X + CmpStripeWidth + 8f + CmpBadgeRadius, origin.Y + CmpCardHeight / 2f);
            DrawRankBadge(badgeCenter, CmpBadgeRadius, spawn.Rank, rankU32, fresh, dl, useTitleFont: false);

            // Same field order as the standard card (just denser) so users
            // don't relearn the layout when switching windows.
            var pull   = PullState(spawn);
            var btnW   = CmpButtonW * 3 + CmpBtnGap * 2;
            var pullW  = pull.Enabled ? 96f : 0f;
            var pullX  = origin.X + width - 8f - pullW;
            var btnX0  = pullX - (pull.Enabled ? 10f : 0f) - btnW;

            if (pull.Enabled)
                DrawPullPanelCompact(dl,
                    new Vector2(pullX, origin.Y + (CmpCardHeight - 40f) / 2f),
                    new Vector2(pullW, 40f), pull);

            var textX     = origin.X + CmpStripeWidth + 8f + CmpBadgeRadius * 2f + 12f;
            var textRight = btnX0 - 12f;
            var y         = origin.Y + CmpCardPad;

            // Row 1 — WORLD (title) + instance badge
            using (Plugin.FontMedium.Push())
            {
                dl.AddText(new Vector2(textX, y),
                    ImGui.GetColorU32(Theme.TextPrimary), spawn.World);
                var ww = ImGui.CalcTextSize(spawn.World).X;
                var cursorX = textX + ww + 8f;
                if (spawn.ZoneInstance > 0)
                    cursorX = DrawInstanceBadge(dl,
                        new Vector2(cursorX, y + 1f), spawn.ZoneInstance) + 8f;
                // mob name trails the world on the same row, secondary
                dl.AddText(new Vector2(cursorX, y + 2f),
                    ImGui.GetColorU32(Theme.TextSecondary), spawn.MobName);
                y += ImGui.GetTextLineHeight() + 4f;
            }

            // Row 2 — meta strip (same builder as the standard card)
            DrawMetaRow(dl, spawn, fresh, new Vector2(textX, y), textRight, compact: true);

            // Actions: primary + neutral secondaries, vertically centred
            var aBtnY = origin.Y + (CmpCardHeight - CmpBtnH) / 2f;
            DrawActionButtonsCompact(spawn, btnX0, aBtnY, spawnKey);
        }

        ImGui.SetCursorScreenPos(origin + new Vector2(0f, CmpCardHeight + 4f));
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
        dl.AddLine(cc + new Vector2(-3f, -3f), cc + new Vector2(3f,  3f), col, 1.5f);
        dl.AddLine(cc + new Vector2(-3f,  3f), cc + new Vector2(3f, -3f), col, 1.5f);

        if (hovered)
        {
            using (ImRaii.Tooltip())
                ImGui.TextUnformatted("Remove this spawn");
        }
        return clicked;
    }

    private static void DrawRankBadge(Vector2 center, float radius, HuntRank rank,
                                      uint rankU32, bool fresh, ImDrawListPtr dl,
                                      bool useTitleFont)
    {
        if (fresh)
        {
            var pulse = 0.5f + 0.5f * MathF.Sin((float)Environment.TickCount / 220f);
            var glowR = radius + 4f + 3f * pulse;
            var glowA = (uint)(0x40 + 0x40 * pulse) << 24 | (rankU32 & 0x00FFFFFF);
            dl.AddCircleFilled(center, glowR, glowA);
        }
        dl.AddCircle(center, radius, rankU32, 0, 2.5f);

        var fontHandle = useTitleFont ? Plugin.FontTitle : Plugin.FontMedium;
        using (fontHandle.Push())
        {
            var label = rank.ToString();   // "S" / "A" / "B"
            var ts    = ImGui.CalcTextSize(label);
            dl.AddText(center - ts * 0.5f, rankU32, label);
        }
    }

    // Small high-contrast "i2" pill next to the world title. Manages its own
    // (medium) font so it stays small even when the caller pushed FontTitle.
    // Returns the pill's right-edge X.
    private static float DrawInstanceBadge(ImDrawListPtr dl, Vector2 topLeft, int instance)
    {
        using (Plugin.FontMedium.Push())
        {
            var label = $"i{instance}";
            var ts    = ImGui.CalcTextSize(label);
            var pad   = new Vector2(7f, 2f);
            var max   = topLeft + ts + pad * 2f;
            dl.AddRectFilled(topLeft, max, ImGui.GetColorU32(Theme.InstanceBg), 4f);
            dl.AddText(topLeft + pad, ImGui.GetColorU32(Theme.InstanceText), label);
            return max.X;
        }
    }

    // Route hint, fixed slot. Returns false if this spawn has no route data
    // (caller leaves the slot blank rather than reflowing the card).
    private static bool DrawRouteHint(SpawnInfo spawn, Vector2 pos, ImDrawListPtr dl)
    {
        if (spawn.ZonePoiId <= 0) return false;
        if (!FaloopRoutes.RouteByPoiId.TryGetValue(spawn.ZonePoiId, out var route)) return false;

        var line = string.IsNullOrEmpty(route.Hint)
            ? $"→ {route.Aetheryte}"
            : $"→ {route.Aetheryte}  ·  {route.Hint}";
        dl.AddText(pos, ImGui.GetColorU32(Theme.RouteHint), line);
        return true;
    }

    // ── Pull state ────────────────────────────────────────────────────

    private readonly struct PullInfo
    {
        public bool   Enabled { get; init; }
        public bool   Ready   { get; init; }
        public string Big     { get; init; }   // "1:23" or "PULL"
        public string Sub     { get; init; }   // "~07:30 ET" or "go!"
        public float  Frac    { get; init; }   // 1→0 drain (countdown remaining)
    }

    private static PullInfo PullState(SpawnInfo spawn)
    {
        var mins = Plugin.Config.PullTimerMinutes;
        if (mins <= 0) return default;   // Enabled = false

        var total     = TimeSpan.FromMinutes(mins);
        var remaining = total - (TimeSync.ServerNow - spawn.ReportedAt);

        if (remaining <= TimeSpan.Zero)
            return new PullInfo { Enabled = true, Ready = true, Big = "PULL", Sub = "go!", Frac = 0f };

        var et = EorzeaClock(DateTime.UtcNow + remaining);
        return new PullInfo
        {
            Enabled = true,
            Ready   = false,
            Big     = MmSs(remaining),
            Sub     = $"~{et} ET",
            Frac    = MathF.Min(1f, (float)(remaining.TotalSeconds / total.TotalSeconds)),
        };
    }

    // Big, glanceable pull panel — the single most time-critical element, so
    // it gets its own framed block with a non-colour state cue (outlined while
    // counting down + draining bar → solid filled pill at PULL).
    private static void DrawPullPanel(ImDrawListPtr dl, Vector2 pos, PullInfo p)
    {
        var max = pos + new Vector2(StdPullW, StdPullH);

        if (p.Ready)
        {
            dl.AddRectFilled(pos, max, ImGui.GetColorU32(Theme.PullReady), 6f);
            CenterText(dl, Plugin.FontTitle, "PULL",
                new Vector2((pos.X + max.X) / 2f, pos.Y + 19f), Theme.PullPanelBg);
            CenterText(dl, Plugin.FontMedium, "go!",
                new Vector2((pos.X + max.X) / 2f, pos.Y + 40f), Theme.PullPanelBg);
            return;
        }

        dl.AddRectFilled(pos, max, ImGui.GetColorU32(Theme.PullPanelBg), 6f);
        dl.AddRect(pos, max, ImGui.GetColorU32(Theme.PullWait), 6f, 0, 1.6f);
        CenterText(dl, Plugin.FontTitle, p.Big,
            new Vector2((pos.X + max.X) / 2f, pos.Y + 16f), Theme.PullWait);
        CenterText(dl, Plugin.FontMedium, p.Sub,
            new Vector2((pos.X + max.X) / 2f, pos.Y + 37f), Theme.TextTertiary);

        // Draining progress bar (shape cue, colour-blind safe).
        var barY = max.Y - 6f;
        var trackA = new Vector2(pos.X + 8f, barY);
        var trackB = new Vector2(max.X - 8f, barY + 2.5f);
        dl.AddRectFilled(trackA, trackB, ImGui.GetColorU32(Theme.PullPanelBorder), 1.5f);
        var fillB = new Vector2(trackA.X + (trackB.X - trackA.X) * p.Frac, trackB.Y);
        dl.AddRectFilled(trackA, fillB, ImGui.GetColorU32(Theme.PullWait), 1.5f);
    }

    private static void DrawPullPanelCompact(ImDrawListPtr dl, Vector2 pos, Vector2 size, PullInfo p)
    {
        var max = pos + size;
        var mid = new Vector2((pos.X + max.X) / 2f, pos.Y + size.Y / 2f - 9f);
        if (p.Ready)
        {
            dl.AddRectFilled(pos, max, ImGui.GetColorU32(Theme.PullReady), 5f);
            CenterText(dl, Plugin.FontMedium, "PULL", mid, Theme.PullPanelBg);
        }
        else
        {
            dl.AddRectFilled(pos, max, ImGui.GetColorU32(Theme.PullPanelBg), 5f);
            dl.AddRect(pos, max, ImGui.GetColorU32(Theme.PullWait), 5f, 0, 1.4f);
            CenterText(dl, Plugin.FontMedium, p.Big, mid, Theme.PullWait);
        }
    }

    // Draw text horizontally centred on cx, with its top at topY.
    private static void CenterText(ImDrawListPtr dl, Dalamud.Interface.ManagedFontAtlas.IFontHandle font,
                                   string text, Vector2 cxTop, Vector4 col)
    {
        using (font.Push())
        {
            var ts = ImGui.CalcTextSize(text);
            dl.AddText(new Vector2(cxTop.X - ts.X / 2f, cxTop.Y), ImGui.GetColorU32(col), text);
        }
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
        var lh       = ImGui.GetTextLineHeight();
        var x        = pos.X;
        var midY     = pos.Y + lh / 2f;

        void Sep()
        {
            if (x > rightX) return;
            dl.AddText(new Vector2(x, pos.Y), sepU, "  ·  ");
            x += ImGui.CalcTextSize("  ·  ").X;
        }
        void Seg(string s, uint col)
        {
            if (string.IsNullOrEmpty(s) || x > rightX) return;
            dl.AddText(new Vector2(x, pos.Y), col, s);
            x += ImGui.CalcTextSize(s).X;
        }

        Seg(spawn.ZoneName, tertiary);
        Sep();

        // Freshness dot: filled = fresh, hollow ring = stale.
        if (x <= rightX)
        {
            var c = new Vector2(x + 4f, midY);
            if (fresh) dl.AddCircleFilled(c, 4f, ImGui.GetColorU32(Theme.AgeFresh));
            else       dl.AddCircle(c, 4f, tertiary, 0, 1.4f);
            x += 13f;
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

        return pos.Y + lh + 4f;
    }

    private static string MmSs(TimeSpan t)
    {
        var total = (int)t.TotalSeconds;
        return $"{total / 60}:{total % 60:D2}";
    }

    // ── Action buttons ────────────────────────────────────────────────

    // One accent PRIMARY (Teleport) + neutral secondaries, so the eye isn't
    // asked to re-read three identical gold chips every glance.
    private static void DrawActionButtons(SpawnInfo spawn, float x0, float y0, long spawnKey)
    {
        var canAct        = spawn.TerritoryId > 0;
        var isTeleporting = TeleportRoutine.InProgress.Contains(spawnKey);
        var x = x0;

        // Primary — Teleport
        if (isTeleporting || !canAct) ImGui.BeginDisabled();
        DrawPrimaryButton(
            isTeleporting ? "Teleporting…" : "Teleport",
            $"##tp_{spawnKey}", new Vector2(x, y0), new Vector2(StdPrimaryW, StdBtnH),
            () => TeleportRoutine.Teleport(spawn));
        if (isTeleporting || !canAct) ImGui.EndDisabled();
        x += StdPrimaryW + StdBtnGap;

        // Secondaries — Flag / Ping / Party
        if (!canAct) ImGui.BeginDisabled();
        DrawNeutralButton("Flag", $"##flag_{spawnKey}",
            new Vector2(x, y0), new Vector2(StdSecondaryW, StdBtnH),
            () => TeleportRoutine.SetFlag(spawn));
        x += StdSecondaryW + StdBtnGap;
        DrawNeutralButton("Ping", $"##ping_{spawnKey}",
            new Vector2(x, y0), new Vector2(StdSecondaryW, StdBtnH),
            () => TeleportRoutine.Ping(spawn));
        if (!canAct) ImGui.EndDisabled();
        x += StdSecondaryW + StdBtnGap;
        DrawNeutralButton("PF", $"##pf_{spawnKey}",
            new Vector2(x, y0), new Vector2(StdSecondaryW, StdBtnH),
            TeleportRoutine.OpenPartyFinder);
    }

    private static void DrawActionButtonsCompact(SpawnInfo spawn, float x0, float y0, long spawnKey)
    {
        var canAct        = spawn.TerritoryId > 0;
        var isTeleporting = TeleportRoutine.InProgress.Contains(spawnKey);
        var x = x0;

        if (isTeleporting || !canAct) ImGui.BeginDisabled();
        DrawPrimaryButton(isTeleporting ? "TP…" : "TP",
            $"##tp_{spawnKey}", new Vector2(x, y0), new Vector2(CmpButtonW, CmpBtnH),
            () => TeleportRoutine.Teleport(spawn));
        if (isTeleporting || !canAct) ImGui.EndDisabled();
        x += CmpButtonW + CmpBtnGap;

        if (!canAct) ImGui.BeginDisabled();
        DrawNeutralButton("Ping", $"##ping_{spawnKey}",
            new Vector2(x, y0), new Vector2(CmpButtonW, CmpBtnH),
            () => TeleportRoutine.Ping(spawn));
        if (!canAct) ImGui.EndDisabled();
        x += CmpButtonW + CmpBtnGap;
        DrawNeutralButton("PF", $"##pf_{spawnKey}",
            new Vector2(x, y0), new Vector2(CmpButtonW, CmpBtnH),
            TeleportRoutine.OpenPartyFinder);
    }

    private static void DrawPrimaryButton(string label, string id, Vector2 pos, Vector2 size, System.Action onClick)
    {
        using (ImRaii.PushColor(ImGuiCol.Button,        Theme.BtnGold))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Theme.BtnGoldHov))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive,  Theme.BtnGoldActv))
        using (ImRaii.PushColor(ImGuiCol.Text,          Theme.BtnGoldText))
        {
            ImGui.SetCursorScreenPos(pos);
            if (ImGui.Button($"{label}{id}", size)) onClick();
        }
    }

    private static void DrawNeutralButton(string label, string id, Vector2 pos, Vector2 size, System.Action onClick)
    {
        using (ImRaii.PushColor(ImGuiCol.Button,        Theme.BtnNeutral))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Theme.BtnNeutralHov))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive,  Theme.BtnNeutralActv))
        using (ImRaii.PushColor(ImGuiCol.Text,          Theme.BtnNeutralText))
        {
            ImGui.SetCursorScreenPos(pos);
            if (ImGui.Button($"{label}{id}", size)) onClick();
        }
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

        Vector2 uvMin = Vector2.Zero, uvMax = Vector2.One;
        var hasCoords = spawn.RawX > 0 && spawn.RawY > 0;
        if (hasCoords)
        {
            var uC   = spawn.RawX / 2048f;
            var vC   = spawn.RawY / 2048f;
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
            uvMin, uvMax, 0xFFFFFFFF, 4f);

        if (thumbHovered)
            dl.AddRect(pos, pos + new Vector2(size, size), 0xFF40D9FF, 4f, 0, 1.5f);

        if (hasCoords)
        {
            var displayU = (spawn.RawX / 2048f - uvMin.X) / (uvMax.X - uvMin.X);
            var displayV = (spawn.RawY / 2048f - uvMin.Y) / (uvMax.Y - uvMin.Y);
            var m        = pos + new Vector2(displayU, displayV) * size;

            // Cross-zone walking arrow — drawn BEFORE the marker so the
            // marker sits on top of the arrowhead tip.
            DrawCrossZoneArrow(dl, spawn, m, pos, size, uvMin, uvMax);

            // In-zone aetheryte you'd teleport to — drawn before the spawn
            // marker so the route reads visually: aetheryte → (connector) → mob.
            DrawAetheryteMarker(dl, spawn, m, pos, size, uvMin, uvMax);

            var pulse     = 0.5f + 0.5f * MathF.Sin((float)Environment.TickCount / 240f);
            var haloR     = 11f + 3.5f * pulse;
            var haloAlpha = (byte)(0x30 + 0x40 * (1f - pulse));
            var haloU32   = ((uint)haloAlpha << 24) | 0x0040DAFFu;
            dl.AddCircleFilled(m, haloR, haloU32);
            dl.AddCircleFilled(m + new Vector2(0.5f, 1f), 6.5f, 0x55000000);
            dl.AddCircleFilled(m, 6f, 0xFF40DAFFu);
            dl.AddCircle      (m, 6f, 0xC0202830u, 0, 1.5f);
            dl.AddCircleFilled(m, 2f, 0xFFFFFFFFu);
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
        var tipPoint   = spawnPos - dir * 10f;
        var arrowBase  = tipPoint - dir * 6.5f;
        var arrowLeft  = arrowBase + perp * 4.2f;
        var arrowRight = arrowBase - perp * 4.2f;

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
            dl.AddCircleFilled(gatewayRaw, 3.5f, LineColor);
            dl.AddCircle      (gatewayRaw, 3.5f, OutlineColor, 0, 1f);
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

        Diamond(a + new Vector2(0.5f, 1f), 5.5f, Shadow);
        Diamond(a, 5f, Blue);

        var an = a + new Vector2(0, -5f);
        var ae = a + new Vector2(5f, 0);
        var as_ = a + new Vector2(0, 5f);
        var aw = a + new Vector2(-5f, 0);
        dl.AddLine(an, ae, Outline, 1.2f);
        dl.AddLine(ae, as_, Outline, 1.2f);
        dl.AddLine(as_, aw, Outline, 1.2f);
        dl.AddLine(aw, an, Outline, 1.2f);

        Diamond(a, 1.8f, 0xFFFFFFFFu);
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
