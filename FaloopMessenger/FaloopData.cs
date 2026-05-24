using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace FaloopMessenger;

// Faloop reference data. The tables themselves (mobs, worlds, zones,
// aetherytes, POI locations, routes) now live in the embedded JSON resource
// Data/faloop-data.json — see FaloopMessenger.csproj. This file only holds the
// loader + the small bits of *logic* (expansion classification, reverse
// territory lookup). Patch-day data refreshes touch the JSON, never this code.
//
// Original tables were extracted from Faloop's main.js, mirrored from
// github.com/SlashNephy/Divination FaloopIntegration plugin (MIT).

public enum MobRank { B, A, S, SS, FATE }

// FFXIV expansions, ordered. Used for the optional per-expansion spawn
// filter — every hunt zone's TerritoryType ID falls cleanly into one bucket.
public enum Expansion { ARR, HW, StB, ShB, EW, DT }

// One phase of a multi-phase (typically SS-precursor / SS-rank) spawn.
// As Faloop emits spawn_progress events advancing the phase, the active
// POI cloud narrows to whichever Phase the new phaseNum points at.
// Grouped=true means "the SS is about to spawn within this small cluster";
// the last phase typically has a single POI and grouped=false (final spot).
public record SpawnPhase(int[] ZonePoiIds, bool Grouped);

public record MobData(uint BNpcId, MobRank Rank, SpawnPhase[]? Phases = null);

// Loads and caches the embedded JSON once. The JsonDocument is held for the
// process lifetime (never disposed) so the parsed JsonElements stay valid.
internal static class FaloopJson
{
    private static readonly JsonDocument Doc = Load();
    public  static JsonElement Root => Doc.RootElement;

    private static JsonDocument Load()
    {
        var asm = typeof(FaloopJson).Assembly;
        using var stream = asm.GetManifestResourceStream("FaloopMessenger.faloop-data.json")
            ?? throw new InvalidOperationException(
                "Embedded resource FaloopMessenger.faloop-data.json is missing");
        return JsonDocument.Parse(stream);
    }
}

public static class FaloopData
{
    // Faloop mob slug → BNpcId + rank
    public static readonly ReadOnlyDictionary<string, MobData> Mobs = BuildMobs();

    // Data center → set of Lumina World row IDs. Used for filtering the global
    // spawn firehose down to a single DC. (NA Aether only verified for now.)
    public static readonly ReadOnlyDictionary<string, HashSet<uint>> DataCenters = BuildDataCenters();

    // Faloop world slug → Lumina World row ID
    public static readonly ReadOnlyDictionary<string, uint> Worlds = BuildUintMap("worlds");

    // Faloop zone slug → Lumina TerritoryType row ID
    public static readonly ReadOnlyDictionary<string, uint> TerritoryTypes = BuildUintMap("territories");

    // Faloop zone slug → aetherytes in that zone (name + raw 2048-scale X/Y).
    // Names match Lifestream's /li accepted names exactly. Zones not in this
    // map have no in-zone aetheryte (e.g. The Dravanian Hinterlands uses
    // Idyllshire from a separate territory — handle those via
    // AetheryteOverrides in TeleportRoutine.cs).
    public static readonly ReadOnlyDictionary<string, (string Name, int X, int Y)[]> ZoneAetherytes = BuildZoneAetherytes();

    // Faloop zone POI ID → raw "x,y" coordinate string (2048-scale)
    public static readonly ReadOnlyDictionary<int, string> Locations = BuildLocations();

    // ── Builders ──────────────────────────────────────────────────────

    private static ReadOnlyDictionary<string, MobData> BuildMobs()
    {
        var d = new Dictionary<string, MobData>();
        foreach (var p in FaloopJson.Root.GetProperty("mobs").EnumerateObject())
        {
            var rank = Enum.Parse<MobRank>(p.Value.GetProperty("rank").GetString()!);
            var bnpc = p.Value.GetProperty("bnpc").GetUInt32();

            // Optional phases array (only on SS ranks + SS-precursor mobs).
            // Drives the spawn_progress narrowing — see HandleSpawnProgress
            // in FaloopSocketClient.
            SpawnPhase[]? phases = null;
            if (p.Value.TryGetProperty("phases", out var ph) && ph.ValueKind == JsonValueKind.Array)
            {
                var list = new List<SpawnPhase>();
                foreach (var phaseEl in ph.EnumerateArray())
                {
                    var ids = new List<int>();
                    if (phaseEl.TryGetProperty("zonePoiIds", out var idsEl) &&
                        idsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var idEl in idsEl.EnumerateArray())
                            if (idEl.ValueKind == JsonValueKind.Number) ids.Add(idEl.GetInt32());
                    }
                    var grouped = phaseEl.TryGetProperty("grouped", out var g) &&
                                  g.ValueKind == JsonValueKind.True;
                    list.Add(new SpawnPhase(ids.ToArray(), grouped));
                }
                phases = list.ToArray();
            }

            d[p.Name] = new MobData(bnpc, rank, phases);
        }
        return new ReadOnlyDictionary<string, MobData>(d);
    }

    private static ReadOnlyDictionary<string, HashSet<uint>> BuildDataCenters()
    {
        var d = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in FaloopJson.Root.GetProperty("dataCenters").EnumerateObject())
        {
            var set = new HashSet<uint>();
            foreach (var id in p.Value.EnumerateArray()) set.Add(id.GetUInt32());
            d[p.Name] = set;
        }
        return new ReadOnlyDictionary<string, HashSet<uint>>(d);
    }

    private static ReadOnlyDictionary<string, uint> BuildUintMap(string section)
    {
        var d = new Dictionary<string, uint>();
        foreach (var p in FaloopJson.Root.GetProperty(section).EnumerateObject())
            d[p.Name] = p.Value.GetUInt32();
        return new ReadOnlyDictionary<string, uint>(d);
    }

    private static ReadOnlyDictionary<string, (string Name, int X, int Y)[]> BuildZoneAetherytes()
    {
        var d = new Dictionary<string, (string, int, int)[]>();
        foreach (var p in FaloopJson.Root.GetProperty("zoneAetherytes").EnumerateObject())
        {
            var list = new List<(string, int, int)>();
            foreach (var a in p.Value.EnumerateArray())
                list.Add((a.GetProperty("name").GetString()!,
                          a.GetProperty("x").GetInt32(),
                          a.GetProperty("y").GetInt32()));
            d[p.Name] = list.ToArray();
        }
        return new ReadOnlyDictionary<string, (string Name, int X, int Y)[]>(d);
    }

    private static ReadOnlyDictionary<int, string> BuildLocations()
    {
        var d = new Dictionary<int, string>();
        foreach (var p in FaloopJson.Root.GetProperty("locations").EnumerateObject())
            d[int.Parse(p.Name)] = p.Value.GetString()!;
        return new ReadOnlyDictionary<int, string>(d);
    }

    // ── Logic ─────────────────────────────────────────────────────────

    // Reverse lookup: TerritoryType uint → Faloop zone slug. Built once on
    // first access from the TerritoryTypes dict.
    private static Dictionary<uint, string>? _slugByTerritory;
    public static string? SlugForTerritory(uint territoryId)
    {
        if (_slugByTerritory == null)
        {
            var d = new Dictionary<uint, string>();
            foreach (var kv in TerritoryTypes) d[kv.Value] = kv.Key;
            _slugByTerritory = d;
        }
        return _slugByTerritory.TryGetValue(territoryId, out var slug) ? slug : null;
    }

    // Ordered (expansion, "territoryId < below") thresholds, loaded from the
    // embedded JSON so a patch-day data correction is a JSON edit, not a code
    // change + re-release. Anything at/above the last threshold (and any new
    // expansion) falls into DT-and-beyond.
    private static readonly (Expansion exp, uint below)[] ExpansionThresholds = BuildExpansionThresholds();

    private static (Expansion exp, uint below)[] BuildExpansionThresholds()
    {
        var list = new List<(Expansion, uint)>();
        foreach (var e in FaloopJson.Root.GetProperty("expansionThresholds").EnumerateArray())
            list.Add((Enum.Parse<Expansion>(e.GetProperty("exp").GetString()!),
                      e.GetProperty("below").GetUInt32()));
        return list.ToArray();
    }

    // Classify a hunt zone's TerritoryType ID into its expansion. The hunt
    // territories are contiguous within each expansion's ID block, so simple
    // ordered thresholds (now data-driven via faloop-data.json) are robust;
    // future expansions get higher IDs and fall into the DT-and-beyond bucket
    // until the table is extended. Returns null for an unknown/zero territory
    // so callers can choose not to filter it out.
    public static Expansion? ExpansionForTerritory(uint territoryId)
    {
        if (territoryId == 0) return null;
        foreach (var (exp, below) in ExpansionThresholds)
            if (territoryId < below) return exp;
        return Expansion.DT;
    }

    // One-line magnitude summary so the maintainer can confirm at a glance that
    // the embedded JSON loaded fully (logged at startup next to the aetheryte
    // audit).
    public static string IntegritySummary() =>
        $"mobs={Mobs.Count} worlds={Worlds.Count} territories={TerritoryTypes.Count} " +
        $"zoneAetherytes={ZoneAetherytes.Count} locations={Locations.Count} " +
        $"routes={FaloopRoutes.RouteByPoiId.Count}";
}
