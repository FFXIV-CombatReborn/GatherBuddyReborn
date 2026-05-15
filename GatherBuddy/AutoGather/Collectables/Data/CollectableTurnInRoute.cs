using System.Numerics;
using GatherBuddy.Vulcan.Vendors;

namespace GatherBuddy.AutoGather.Collectables.Data;

public sealed class CollectableTurnInRoute
{
    public uint                    ShopId      { get; set; }
    public uint                    NpcId       { get; set; }
    public string                  NpcName     { get; set; } = string.Empty;
    public uint                    TerritoryId { get; set; }
    public uint                    MapRowId    { get; set; }
    public Vector3                 Location    { get; set; }
    public VendorNpcLocationSource Source      { get; set; } = VendorNpcLocationSource.Unknown;
}
