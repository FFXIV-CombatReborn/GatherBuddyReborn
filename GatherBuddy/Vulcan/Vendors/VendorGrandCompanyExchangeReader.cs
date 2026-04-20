using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;

namespace GatherBuddy.Vulcan.Vendors;

public unsafe class VendorGrandCompanyExchangeReader(AtkUnitBase* addon) : AtkReader(addon)
{
    public uint ItemCount
        => ReadUInt(1).GetValueOrDefault();

    public List<VendorGrandCompanyExchangeItemReader> Items
        => [.. Loop<VendorGrandCompanyExchangeItemReader>(17, 1, (int)ItemCount)];
}

public class VendorGrandCompanyExchangeItemReader(IntPtr addon, int beginOffset = 0) : AtkReader(addon, beginOffset)
{
    public string Name
        => ReadSeString(0).TextValue;

    public uint SealCost
        => ReadUInt(50).GetValueOrDefault();

    public uint IconId
        => ReadUInt(150).GetValueOrDefault();

    public bool Stackable
        => ReadBool(250).GetValueOrDefault();

    public uint ItemId
        => ReadUInt(300).GetValueOrDefault();

    public uint RequiredRank
        => ReadUInt(400).GetValueOrDefault();

    public bool OpensCurrencyExchange
        => ReadBool(450).GetValueOrDefault();

    public int Index
        => beginOffset - 17;
}
