using System;

namespace GatherBuddy.Vulcan.Vendors;

public sealed class VendorBuyListEntry
{
    public Guid               Id             { get; set; } = Guid.NewGuid();
    public uint               ItemId         { get; set; }
    public string             ItemName       { get; set; } = string.Empty;
    public ushort             IconId         { get; set; }
    public uint               Cost           { get; set; }
    public uint               CurrencyItemId { get; set; }
    public string             CurrencyName   { get; set; } = string.Empty;
    public VendorShopType     ShopType       { get; set; } = VendorShopType.GilShop;
    public uint               SourceShopId   { get; set; }
    public int                ShopItemIndex  { get; set; } = -1;
    public int                GcRankIndex    { get; set; } = -1;
    public int                GcCategoryIndex { get; set; } = -1;
    public uint               VendorNpcId    { get; set; }
    public string             VendorNpcName  { get; set; } = string.Empty;
    public VendorMenuShopType MenuShopType   { get; set; } = VendorMenuShopType.GilShop;
    public uint               ShopId         { get; set; }
    public uint               TargetQuantity { get; set; } = 1;
}
