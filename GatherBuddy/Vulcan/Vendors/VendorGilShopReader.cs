using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;

namespace GatherBuddy.Vulcan.Vendors;

public unsafe class VendorGilShopReader(AtkUnitBase* addon) : AtkReader(addon)
{
    public uint PurchasableItemCount
        => ReadUInt(2).GetValueOrDefault();

    public List<VendorGilShopItemReader> Items
        => [.. Loop<VendorGilShopItemReader>(14, 1, (int)PurchasableItemCount)];
}

public class VendorGilShopItemReader(IntPtr addon, int beginOffset = 0) : AtkReader(addon, beginOffset)
{

    public uint GilCost
        => ReadUInt(61).GetValueOrDefault();

    public uint ItemId
        => ReadUInt(427).GetValueOrDefault();

    public uint Index
        => (uint)(beginOffset - 14);
}
