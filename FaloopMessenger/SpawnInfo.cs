using System;

namespace FaloopMessenger;

public enum HuntRank { S, A, B }

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

    public bool      IsDead   { get; set; }
    public DateTime? KilledAt { get; set; }
}
