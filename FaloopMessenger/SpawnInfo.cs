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

    // When a scheduled/early-access spawn transitions to public, the upsert
    // path stamps this with ServerNow. Renderer shows a green JUST RELEASED
    // badge for ~10 minutes after the stamp so the transition is visible
    // (otherwise the card just silently loses its pre-release badge, which
    // is indistinguishable from "the card never updated").
    public DateTime? PublicReleasedAt { get; init; }

    public bool      IsDead   { get; init; }
    public DateTime? KilledAt { get; init; }
}
