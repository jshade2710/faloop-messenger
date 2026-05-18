using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace FaloopMessenger;

// One-shot routing data. Each mob spawn POI has a precomputed best gateway
// (aetheryte) and an optional "how to get there" hint (e.g. "walk to Middle
// La Noscea" or "gate to Old Sharlayan"). For cross-zone routes
// (boundary / gatekeeper), GatewayX/Y is the 2048-scale pixel coordinate of
// the boundary point on the SPAWN's zone map — used to draw an arrow from the
// entry point to the spawn marker.
//
// Encodes the same shortest-route logic the Faloop website / Discord bot use.
// The table lives in the embedded Data/faloop-data.json ("routes" section);
// this file is just the loader + the record shape.
public record FaloopRoute(string Aetheryte, string? Hint, int GatewayX, int GatewayY);

public static class FaloopRoutes
{
    public static readonly ReadOnlyDictionary<int, FaloopRoute> RouteByPoiId = Build();

    private static ReadOnlyDictionary<int, FaloopRoute> Build()
    {
        var d = new Dictionary<int, FaloopRoute>();
        foreach (var p in FaloopJson.Root.GetProperty("routes").EnumerateObject())
        {
            var v    = p.Value;
            var hint = v.GetProperty("hint");
            d[int.Parse(p.Name)] = new FaloopRoute(
                v.GetProperty("aetheryte").GetString()!,
                hint.ValueKind == JsonValueKind.Null ? null : hint.GetString(),
                v.GetProperty("gx").GetInt32(),
                v.GetProperty("gy").GetInt32());
        }
        return new ReadOnlyDictionary<int, FaloopRoute>(d);
    }
}
