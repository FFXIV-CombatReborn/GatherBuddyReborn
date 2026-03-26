using System.Collections.Generic;

namespace GatherBuddy.Marketboard;

public sealed class MarketListing
{
    public int    PricePerUnit { get; init; }
    public int    Quantity     { get; init; }
    public bool   IsHq         { get; init; }
    public string WorldName    { get; init; } = string.Empty;
}

public sealed class MarketItemData
{
    public uint   ItemId                { get; init; }
    public string ItemName              { get; set;  } = string.Empty;
    public uint   IconId                { get; set;  }
    public float  MinPrice              { get; init; }
    public List<MarketListing> Listings { get; init; } = new();
}
