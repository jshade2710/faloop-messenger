using System;
using System.Collections.Generic;

namespace FaloopMessenger;

public enum HuntRank { S, A, B }

// One reported spawn point: raw 2048-scale (for the map thumbnail UV) plus
// the in-game map coords (for clickable chat MapLinkPayloads).
public readonly record struct SpawnPoint(int RawX, int RawY, float MapX, float MapY);

// v0.4.7: now a record class. Every state change (location refinement, death,
// progress, release) constructs a NEW SpawnInfo via `with`; nothing mutates an
// existing instance after it's stored in _spawns. That makes the volatile
// snapshot pattern safe — readers (the render thread) only ever see fully-
// constructed objects, so no torn reads, no concurrent List<SpawnPoint>
// enumeration with a Clear()/Add() in flight. See C-1 in the v0.4.7 audit.
public record class SpawnInfo
{
    // H-1 (v0.4.14 review): identity comes from the WIRE, not from Lumina
    // resolution. MobName/World below are display strings resolved through
    // Lumina — and that resolution can fall back to the raw slug when a
    // sheet is briefly unready or an ID is unknown. Keying the upsert on
    // display strings meant two events for the same mark could produce two
    // cards ("forgiven_rebellion" vs "Forgiven Rebellion"), one of them
    // coordless — the suspected root of the origin-flag bug. These two
    // slugs are copied verbatim from the Faloop payload and never require
    // a lookup, so the identity key is stable across every event.
    public required string MobSlug   { get; init; }
    public required string WorldSlug { get; init; }

    public required string World    { get; init; }
    public required string MobName  { get; init; }
    public required string ZoneName { get; init; }
    public required float  X        { get; init; }
    public required float  Y        { get; init; }
    public HuntRank  Rank         { get; init; } = HuntRank.S;
    public int       HpPercent    { get; init; } = 100;
    public string    Reporter     { get; init; } = string.Empty;
    public DateTime  ReportedAt   { get; init; } = DateTime.Now;
    public string    RawEvent     { get; init; } = string.Empty;
    public int       ZoneInstance { get; init; }

    // Resolved after parsing — used for map flag
    public uint TerritoryId { get; init; }
    public uint MapId       { get; init; }

    // Raw 2048-scale pixel coords from Faloop — used to place a marker on the
    // map texture (which is also 2048×2048). 0,0 means "no precise location yet".
    public int RawX { get; init; }
    public int RawY { get; init; }

    // Faloop's zone POI ID for this spawn (used to look up the precomputed
    // travel route). 0 = unknown.
    public int ZonePoiId { get; init; }

    // All reported points. A normal S-rank has one (== RawX/RawY); SS "minion"
    // reports come in at several POIs at once — every one is drawn on the map,
    // and (on-world) echoed as its own clickable flag. IReadOnlyList so the
    // renderer can't accidentally mutate it; producers build a fresh List
    // every time and hand it in.
    public IReadOnlyList<SpawnPoint> Points { get; init; } = System.Array.Empty<SpawnPoint>();

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

    // Transient flag set by the socket-client upsert path when a spawn we
    // were already tracking as scheduled/early-access just received its
    // first non-scheduled event — the authoritative "now publicly live"
    // signal. Drives the second ding + "[Public release]" echo prefix so
    // users who muted the pre-release ping get a real alert at pull time.
    // Not persisted; only meaningful for the single OnNewSpawn invocation.
    public bool JustWentPublic { get; init; }

    // Transient flag set by socket-client upsert / location-refinement /
    // release paths when a spawn we were already tracking at (0,0)
    // receives real coordinates for the first time. Triggers a re-fire
    // of OnNewSpawn so the user gets a chat echo with a clickable map
    // flag — the initial echo (prior to coords) only printed the prefix
    // line, so without this they'd never get a flag link for that spawn.
    public bool CoordsRevealed { get; init; }

    // Transient flag set when a spawn that ALREADY had coordinates gets
    // corrected to a meaningfully different position (Faloop reporters
    // refining / fixing a location). Debounced by a distance threshold in
    // the socket client so sub-tile refinements don't spam chat — only a
    // move of more than a couple map-units re-echoes. Distinct from
    // CoordsRevealed (0→real) so the echo prefix can say "updated".
    public bool CoordsCorrected { get; init; }

    // When a scheduled/early-access spawn transitions to public, the upsert
    // path stamps this with ServerNow. Renderer shows a green JUST RELEASED
    // badge for ~10 minutes after the stamp so the transition is visible
    // (otherwise the card just silently loses its pre-release badge, which
    // is indistinguishable from "the card never updated").
    public DateTime? PublicReleasedAt { get; init; }

    public bool      IsDead   { get; init; }
    public DateTime? KilledAt { get; init; }

    // True when this spawn has a real, plantable map location. Faloop reports
    // a spawn before it always knows where the mob is — a scheduled/pre-
    // release event, or a report Faloop hasn't located yet, arrives with no
    // usable coordinates. Raw (0,0) is Faloop's "unknown" sentinel; it
    // resolves to map ~(1.0,1.0), the near-origin spot a mis-planted flag
    // lands on. We gate on the RAW coords (authoritative reported position)
    // plus territory/map resolution so both the (0,0) and (0.1,0.1) cases
    // read as "not ready". Consumed by the flag buttons to red-flag an
    // un-plantable spawn, and mirrors SetFlag's own plant guard.
    public bool HasLocation
    {
        get
        {
            if (TerritoryId == 0 || MapId == 0) return false;
            if (Points.Count > 0) return Points[0].RawX > 0 || Points[0].RawY > 0;
            return RawX > 0 || RawY > 0;
        }
    }
}
