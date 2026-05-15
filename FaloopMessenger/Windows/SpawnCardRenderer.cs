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
    private const float StdCardHeight   = 116f;
    private const float StdCardPad      = 10f;
    private const float StdStripeWidth  = 4f;
    private const float StdBadgeRadius  = 20f;
    private const float StdThumbSize    = 100f;
    private const float StdButtonW      = 80f;
    private const float StdBtnH         = 22f;
    private const float StdBtnGap       = 4f;

    private const float CmpCardHeight   = 64f;
    private const float CmpCardPad      = 6f;
    private const float CmpStripeWidth  = 3f;
    private const float CmpBadgeRadius  = 12f;
    private const float CmpButtonW      = 56f;
    private const float CmpBtnH         = 20f;
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
        var parts    = new List<string> { spawn.MobName, spawn.World, spawn.ZoneName };
        if (!string.IsNullOrEmpty(spawn.Reporter)) parts.Add($"reporter: {spawn.Reporter}");
        parts.Add($"killed {FormatAge(age)} ago");
        var text = string.Join("  ·  ", parts);

        var textY = origin.Y + (DeadH - ImGui.GetTextLineHeight()) / 2f;
        dl.AddText(new Vector2(badgeC.X + 14f, textY), mutedU, text);

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

        // ImRaii.PushId auto-pops on dispose — even if something inside the
        // card render throws — so the ImGui ID stack can never get unbalanced
        // (an unbalanced stack corrupts ImGui and can hard-crash the game).
        using (ImRaii.PushId($"card_{spawnKey}"))
        {
            var hovered = ImGui.IsMouseHoveringRect(origin, origin + new Vector2(width, StdCardHeight));

            // Card background — fade-in flash that resolves to normal slate
            var bgNormal = hovered ? Theme.CardBgHov : Theme.CardBg;
            var bgFlash  = new Vector4(rankCol.X, rankCol.Y, rankCol.Z, 0.35f);
            var bg       = Vector4.Lerp(bgFlash, bgNormal, fadeFrac);
            dl.AddRectFilled(origin, origin + new Vector2(width, StdCardHeight),
                ImGui.GetColorU32(bg), CardRound);
            dl.AddRectFilled(origin, origin + new Vector2(StdStripeWidth, StdCardHeight),
                rankU32, CardRound);

            // × dismiss button (top-LEFT, just inside the stripe)
            if (DrawCloseButton(origin, StdStripeWidth + 4f, 4f, 14f, spawnKey, dl))
            {
                client.RemoveSpawn(spawn);
                _firstRenderAt.Remove(spawnKey);
                TeleportRoutine.InProgress.Remove(spawnKey);
                return;
            }

            // Rank badge with fresh pulse
            var badgeCenter = origin + new Vector2(StdStripeWidth + 8f + StdBadgeRadius, StdCardHeight / 2f);
            DrawRankBadge(badgeCenter, StdBadgeRadius, spawn.Rank, rankU32, fresh, dl, useTitleFont: true);

            // Text block
            var textX = origin.X + StdStripeWidth + 12f + StdBadgeRadius * 2f + 12f;
            var textY = origin.Y + StdCardPad;

            using (Plugin.FontTitle.Push())
            {
                dl.AddText(new Vector2(textX, textY), rankU32, spawn.MobName);
                textY += ImGui.GetTextLineHeight() + 2f;
            }

            using (Plugin.FontMedium.Push())
            {
                var (chipMax, _) = DrawWorldChip(spawn.World, new Vector2(textX, textY), dl);
                dl.AddText(new Vector2(chipMax.X + 8f, textY + 2f),
                    ImGui.GetColorU32(Theme.Subtle), spawn.ZoneName);
                textY = chipMax.Y + 4f;
            }

            var ageStr = FormatAge(TimeSync.ServerNow - spawn.ReportedAt);
            var coords = $"({spawn.X:F1}, {spawn.Y:F1})  ·  {ageStr}";
            var ageCol = fresh ? Theme.AgeFresh : Theme.Subtle;
            dl.AddText(new Vector2(textX, textY), ImGui.GetColorU32(ageCol), coords);

            var pull = PullSegment(spawn);
            if (pull.HasValue)
            {
                var cw = ImGui.CalcTextSize(coords).X;
                dl.AddText(new Vector2(textX + cw, textY),
                    ImGui.GetColorU32(pull.Value.col), $"  ·  {pull.Value.text}");
            }
            textY += ImGui.GetTextLineHeight() + 2f;

            if (!string.IsNullOrEmpty(spawn.Reporter))
            {
                dl.AddText(new Vector2(textX, textY),
                    ImGui.GetColorU32(Theme.Muted),
                    $"Reporter: {spawn.Reporter}");
                textY += ImGui.GetTextLineHeight() + 2f;
            }

            DrawRouteHint(spawn, new Vector2(textX, textY), dl);

            // Map thumbnail
            var thumbX = origin.X + width - 10f - StdButtonW - 14f - StdThumbSize;
            var thumbY = origin.Y + (StdCardHeight - StdThumbSize) / 2f;
            DrawMapThumb(spawn, new Vector2(thumbX, thumbY), StdThumbSize);

            // Action buttons (stacked vertically)
            var btnX  = origin.X + width - StdButtonW - 10f;
            var btnY0 = origin.Y + (StdCardHeight - (StdBtnH * 3 + StdBtnGap * 2)) / 2f;
            DrawActionButtons(spawn, btnX, btnY0, StdButtonW, StdBtnH, StdBtnGap,
                              stacked: true, spawnKey: spawnKey);
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

            // × dismiss (top-left)
            if (DrawCloseButton(origin, CmpStripeWidth + 3f, 3f, 12f, spawnKey, dl))
            {
                client.RemoveSpawn(spawn);
                _firstRenderAt.Remove(spawnKey);
                TeleportRoutine.InProgress.Remove(spawnKey);
                return;
            }

            // Smaller rank badge
            var badgeCenter = origin + new Vector2(CmpStripeWidth + 6f + CmpBadgeRadius, CmpCardHeight / 2f);
            DrawRankBadge(badgeCenter, CmpBadgeRadius, spawn.Rank, rankU32, fresh, dl, useTitleFont: false);

            var textX = origin.X + CmpStripeWidth + 8f + CmpBadgeRadius * 2f + 10f;
            var textY = origin.Y + CmpCardPad;

            // Line 1: mob name + world chip + age (all on one row)
            using (Plugin.FontMedium.Push())
            {
                var nameSize = ImGui.CalcTextSize(spawn.MobName);
                dl.AddText(new Vector2(textX, textY), rankU32, spawn.MobName);
                var afterName = textX + nameSize.X + 8f;

                var (chipMax, _) = DrawWorldChip(spawn.World, new Vector2(afterName, textY), dl);

                var ageStr  = FormatAge(TimeSync.ServerNow - spawn.ReportedAt);
                var ageCol  = fresh ? Theme.AgeFresh : Theme.Subtle;
                var ageText = $"· {ageStr}";
                dl.AddText(new Vector2(chipMax.X + 8f, textY + 2f),
                    ImGui.GetColorU32(ageCol), ageText);

                var pull = PullSegment(spawn);
                if (pull.HasValue)
                {
                    var aw = ImGui.CalcTextSize(ageText).X;
                    dl.AddText(new Vector2(chipMax.X + 8f + aw + 6f, textY + 2f),
                        ImGui.GetColorU32(pull.Value.col), pull.Value.text);
                }

                textY = chipMax.Y + 4f;
            }

            // Line 2: route hint (or zone fallback)
            if (!DrawRouteHint(spawn, new Vector2(textX, textY), dl))
                dl.AddText(new Vector2(textX, textY), ImGui.GetColorU32(Theme.Subtle), spawn.ZoneName);

            // Action buttons in a horizontal row on the right
            var totalBtnW = CmpButtonW * 3 + CmpBtnGap * 2;
            var btnX0     = origin.X + width - totalBtnW - 8f;
            var btnY      = origin.Y + (CmpCardHeight - CmpBtnH) / 2f;
            DrawActionButtons(spawn, btnX0, btnY, CmpButtonW, CmpBtnH, CmpBtnGap,
                              stacked: false, spawnKey: spawnKey);
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

    // Renders a small pill-shaped chip with the world name. Returns the chip's
    // bounding-box max corner and the calculated text size.
    private static (Vector2 max, Vector2 textSize) DrawWorldChip(string world, Vector2 topLeft, ImDrawListPtr dl)
    {
        var ts      = ImGui.CalcTextSize(world);
        var chipPad = new Vector2(8f, 2f);
        var max     = topLeft + ts + chipPad * 2f;
        dl.AddRectFilled(topLeft, max, ImGui.GetColorU32(Theme.ChipBg), 4f);
        dl.AddText(topLeft + chipPad, ImGui.GetColorU32(Vector4.One), world);
        return (max, ts);
    }

    // Returns true if a route hint was drawn (false if no route data for this spawn).
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

    private static void DrawActionButtons(SpawnInfo spawn, float x0, float y0,
                                          float btnW, float btnH, float gap,
                                          bool stacked, long spawnKey)
    {
        var canFlag       = spawn.TerritoryId > 0;
        var isTeleporting = TeleportRoutine.InProgress.Contains(spawnKey);

        using (ImRaii.PushColor(ImGuiCol.Button,        Theme.BtnGold))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Theme.BtnGoldHov))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive,  Theme.BtnGoldActv))
        using (ImRaii.PushColor(ImGuiCol.Text,          Theme.BtnGoldText))
        {
            if (!canFlag) ImGui.BeginDisabled();

            // Helper to position each button
            void PlaceButton(int idx)
            {
                var offset = idx * (stacked ? (btnH + gap) : (btnW + gap));
                var pos    = stacked ? new Vector2(x0, y0 + offset)
                                      : new Vector2(x0 + offset, y0);
                ImGui.SetCursorScreenPos(pos);
            }

            PlaceButton(0);
            if (ImGui.Button($"Party##party_{spawnKey}", new Vector2(btnW, btnH)))
                TeleportRoutine.OpenPartyFinder();

            PlaceButton(1);
            if (ImGui.Button($"Ping##ping_{spawnKey}", new Vector2(btnW, btnH)))
                TeleportRoutine.Ping(spawn);

            PlaceButton(2);
            if (isTeleporting) ImGui.BeginDisabled();
            var tpLabel = isTeleporting ? "TP'ing…" : "TP";
            if (ImGui.Button($"{tpLabel}##tp_{spawnKey}", new Vector2(btnW, btnH)))
                TeleportRoutine.Teleport(spawn);
            if (isTeleporting) ImGui.EndDisabled();

            if (!canFlag) ImGui.EndDisabled();
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

    private static string FormatAge(TimeSpan age) =>
        age.TotalHours >= 1
            ? $"{(int)age.TotalHours}h{age.Minutes:D2}m"
            : age.TotalMinutes >= 1
                ? $"{(int)age.TotalMinutes}m{age.Seconds:D2}s"
                : $"{age.Seconds}s";

    // Hunt-train pull timer segment, or null when the timer is disabled.
    // Counts down "pull in Xm Ys" (amber) then flips to "PULL" (green).
    private static (string text, Vector4 col)? PullSegment(SpawnInfo spawn)
    {
        var mins = Plugin.Config.PullTimerMinutes;
        if (mins <= 0) return null;

        var remaining = TimeSpan.FromMinutes(mins) - (TimeSync.ServerNow - spawn.ReportedAt);
        return remaining > TimeSpan.Zero
            ? ($"pull in {FormatAge(remaining)}", Theme.PullWait)
            : ("PULL", Theme.PullReady);
    }
}
