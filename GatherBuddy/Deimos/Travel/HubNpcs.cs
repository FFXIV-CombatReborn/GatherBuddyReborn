using System.Numerics;

namespace GatherBuddy.Deimos.Travel;

internal static class HubNpcs
{
    public enum Kind
    {
        Repair,
        Gamba,
        RedAlert, // teleports you near the active critical site
    }

    public readonly record struct Entry(uint NpcId, Vector3 Position);

    public static Entry? Get(uint territoryId, Kind kind) => (territoryId, kind) switch
    {
        (CosmicZone.SinusArdorum, Kind.Repair) => new Entry(1052610, new(19.46f, 1.69f, 18.11f)),
        (CosmicZone.SinusArdorum, Kind.Gamba)  => new Entry(1052612, new(18.84f, 2.24f, -18.91f)),
        (CosmicZone.Phaenna, Kind.Repair)      => new Entry(1052641, new(359.52f, 52.75f, -401.72f)),
        (CosmicZone.Phaenna, Kind.Gamba)       => new Entry(1052642, new(358.82f, 53.19f, -438.86f)),
        (CosmicZone.Oizys, Kind.Repair)        => new Entry(1052651, new(-202.44f, 0.65f, 154.31f)),
        (CosmicZone.Oizys, Kind.Gamba)         => new Entry(1052652, new(-157.73f, 1.19f, 153.98f)),
        (CosmicZone.Auxesia, Kind.Repair)      => new Entry(1056825, new(317.60f, 205.75f, 374.77f)),
        (CosmicZone.Auxesia, Kind.Gamba)       => new Entry(1056826, new(290.94f, 206.21f, 349.35f)),
        (CosmicZone.SinusArdorum, Kind.RedAlert) => new Entry(1052663, new(16.74f, 1.71f, -3.86f)),
        (CosmicZone.Phaenna, Kind.RedAlert)      => new Entry(1052626, new(343.89f, 52.64f, -443.47f)),
        (CosmicZone.Oizys, Kind.RedAlert)        => new Entry(1052645, new(-155.02f, 0.50f, 144.58f)),
        (CosmicZone.Auxesia, Kind.RedAlert)      => new Entry(1056819, new(280.41f, 205.64f, 352.49f)),
        _ => null,
    };
}
