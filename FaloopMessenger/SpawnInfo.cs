using System;
using System.Collections.Generic;

namespace FaloopMessenger;

public enum HuntRank { S, A, B }

// One reported spawn point: raw 2048-scale (for the map thumbnail UV) plus
// the in-game map coords (for clickable chat MapLinkPayloads).
public readonly record struct SpawnPoint(int RawX, int RawY, float MapX, float MapY);

public class SpawnInfo
{
    public required string World    { get; init; }
    public required string MobName  { get; init; }
    public required string ZoneName { get; init; }
    public required float  X        { get; set; }
    public required float  Y        { get; set; }
    public HuntRank  Rank         { get; init; } = HuntRank.S;
    public int       HpPercent    { get; init; } = 100;
    public string    Reporter     { get; init; } = string.Empty;
    public DateTime  ReportedAt   { get; init; } = DateTime.Now;
    public string    RawEvent     { get; init; } = string.Empty;
    public int       ZoneInstance { get; init; }

    // Resolved after parsing — used for map flag
    public uint TerritoryId { get; set; }
    public uint MapId       { get; set; }

    // Raw 2048-scale pixel coords from Faloop — used to place a marker on the
    // map texture (which is also 2048×2048). 0,0 means "no precise location yet".
    public int RawX { get; set; }
    public int RawY { get; set; }

    // Faloop's zone POI ID for this spawn (used to look up the precomputed
    // travel route). 0 = unknown.
    public int ZonePoiId { get; set; }

    // All reported points. A normal S-rank has one (== RawX/RawY); SS "minion"
    // reports come in at several POIs at once — every one is drawn on the map,
    // and (on-world) echoed as its own clickable flag.
    public List<SpawnPoint> Points { get; init; } = new();

    // True for SS-rank marks (e.g. Forgiven Rebellion, Ker). They still sit in
    // the S tier for every filter/window — only the badge differs — so adding
    // this can't make them vanish anywhere S-ranks are shown.
    public bool IsSS { get; init; }

    // Faloop's "isScheduled" flag: a scheduled (pre-release) report — surfaced
    // before it's publicly released. Normal public reports have it false.
    public bool IsScheduled { get; init; }

    // Faloop's "scheduleDelay" — seconds between the report and its public
    // release. The card counts down ReportedAt + ScheduleDelay, then the
    // PRE-RELEASE flag flips to RELEASED. Null/absent = no delay (0).
    public int? ScheduleDelay { get; init; }

    // Faloop's "stage" field on a spawn event. Pre-release events arrive with
    // stage: 1 (or other non-null int). When a privileged reporter pushes
    // "manual release", Faloop re-emits the spawn with stage: null — that's
    // the authoritative signal that the pre-release window is over and the
    // mob is publicly live, independent of clock skew or scheduleDelay math.
    public int? Stage { get; init; }

    public bool      IsDead   { get; set; }
    public DateTime? KilledAt { get; set; }
}
