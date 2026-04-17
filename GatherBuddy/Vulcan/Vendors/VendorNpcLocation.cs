using System.Numerics;

namespace GatherBuddy.Vulcan.Vendors;

public sealed record VendorNpcLocation(
    uint    NpcId,
    string  NpcName,
    uint    TerritoryId,
    uint    MapRowId,
    Vector3 Position
);
