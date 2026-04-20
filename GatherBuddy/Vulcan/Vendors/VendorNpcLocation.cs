using System.Numerics;

namespace GatherBuddy.Vulcan.Vendors;
public enum VendorNpcLocationSource : byte
{
    Override,
    Unknown,
    DataShare,
    Level,
    Supplemental,
    Lgb,
}

public sealed record VendorNpcLocation(
    uint                    NpcId,
    string                  NpcName,
    uint                    TerritoryId,
    uint                    MapRowId,
    Vector3                 Position,
    VendorNpcLocationSource Source = VendorNpcLocationSource.Unknown
);
