using System.Collections.Generic;

namespace GatherBuddy.Vulcan.Vendors;

public enum VendorShopType
{
    GilShop,
    SpecialCurrency,
    GrandCompanySeals,
}

public enum VendorMenuShopType
{
    GilShop,
    SpecialShop,
    InclusionShop,
    CollectablesShop,
    GrandCompanyShop,
    FreeCompanyCreditShop,
}

public enum VendorGilFilter { All, Gatherable, Fish, Craftable, Housing, Dyes, Other }

public enum VendorCurrencyGroup
{
    Gil,
    Tomestones,
    BicolorGemstones,
    HuntSeals,
    GrandCompanySeals,
    Scrips,
    MGP,
    PvP,
    Other,
}

public sealed record VendorNpc(
    uint               NpcId,
    string             Name,
    uint               ShopId,
    VendorMenuShopType MenuShopType       = VendorMenuShopType.GilShop,
    int                InclusionPageIndex = -1,
    int                InclusionSubPageIndex = -1,
    int                ShopItemIndex         = -1,
    uint               SourceShopId          = 0,
    int                GcRankIndex           = -1,
    int                GcCategoryIndex       = -1
);

public sealed record VendorShopEntry(
    uint                ItemId,
    string              ItemName,
    ushort              IconId,
    uint                Cost,
    uint                CurrencyItemId,
    string              CurrencyName,
    List<VendorNpc>     Npcs,
    VendorShopType      ShopType,
    VendorCurrencyGroup Group
);
