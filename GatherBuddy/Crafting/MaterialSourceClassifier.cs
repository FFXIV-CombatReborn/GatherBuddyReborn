using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

public enum MaterialSource
{
    Gatherable,
    Fish,
    Scrip,
    GilVendor,
    SpecialCurrency,
    Craftable,
    Drop,
    Other
}

public static class MaterialSourceClassifier
{
    private static HashSet<uint>? _gilVendorItems;
    private static HashSet<uint>? _specialCurrencyItems;
    private static HashSet<uint>? _craftableItems;
    private static HashSet<uint>? _dropItems;
    private static bool _initialized;

    public static void Reset()
    {
        _gilVendorItems       = null;
        _specialCurrencyItems = null;
        _craftableItems       = null;
        _dropItems            = null;
        _initialized          = false;
    }

    public static MaterialSource Classify(uint itemId, bool preferVendors = false)
    {
        EnsureInitialized();

        if (preferVendors && _gilVendorItems?.Contains(itemId) == true)
            return MaterialSource.GilVendor;

        if (GatherBuddy.GameData.Gatherables.ContainsKey(itemId))
            return MaterialSource.Gatherable;

        if (GatherBuddy.GameData.Fishes.ContainsKey(itemId))
            return MaterialSource.Fish;

        if (ScripShopItemManager.ShopItems.Any(s => s.ItemId == itemId))
            return MaterialSource.Scrip;

        if (_dropItems?.Contains(itemId) == true)
            return MaterialSource.Drop;

        if (_craftableItems?.Contains(itemId) == true)
            return MaterialSource.Craftable;

        if (_gilVendorItems?.Contains(itemId) == true)
            return MaterialSource.GilVendor;

        if (_specialCurrencyItems?.Contains(itemId) == true)
            return MaterialSource.SpecialCurrency;

        return MaterialSource.Other;
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        BuildGilVendorSet();
        BuildSpecialCurrencySet();
        BuildCraftableSet();
        BuildDropSet();
    }

    private static void BuildGilVendorSet()
    {
        _gilVendorItems = new HashSet<uint>();
        try
        {
            var sheet = Dalamud.GameData.GetSubrowExcelSheet<GilShopItem>();
            if (sheet == null) return;
            foreach (var subrow in sheet.SelectMany(s => s))
                if (subrow.Item.RowId > 0)
                    _gilVendorItems.Add(subrow.Item.RowId);
            GatherBuddy.Log.Debug($"[MaterialSourceClassifier] Gil vendor set: {_gilVendorItems.Count} items");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[MaterialSourceClassifier] Gil vendor set failed: {ex.Message}");
        }
    }

    private static void BuildCraftableSet()
    {
        _craftableItems = new HashSet<uint>();
        try
        {
            var sheet = Dalamud.GameData.GetExcelSheet<Recipe>();
            if (sheet == null) return;
            foreach (var recipe in sheet)
                if (recipe.ItemResult.RowId > 0)
                    _craftableItems.Add(recipe.ItemResult.RowId);
            GatherBuddy.Log.Debug($"[MaterialSourceClassifier] Craftable set: {_craftableItems.Count} items");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[MaterialSourceClassifier] Craftable set failed: {ex.Message}");
        }
    }

    private static void BuildDropSet()
    {
        _dropItems = new HashSet<uint>();
        try
        {
            var sheet = Dalamud.GameData.GetExcelSheet<RetainerTaskNormal>();
            if (sheet == null) return;
            foreach (var row in sheet)
                if (row.Item.RowId > 0 && row.GatheringLog.RowId == 0 && row.FishingLog.RowId == 0)
                    _dropItems.Add(row.Item.RowId);
            GatherBuddy.Log.Debug($"[MaterialSourceClassifier] Drop set: {_dropItems.Count} items");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[MaterialSourceClassifier] Drop set failed: {ex.Message}");
        }
    }

    private static void BuildSpecialCurrencySet()
    {
        _specialCurrencyItems = new HashSet<uint>();
        try
        {
            var sheet = Dalamud.GameData.GetExcelSheet<SpecialShop>();
            if (sheet == null) return;
            foreach (var shop in sheet)
            {
                foreach (var entry in shop.Item)
                {
                    foreach (var received in entry.ReceiveItems)
                    {
                        var id = received.Item.RowId;
                        if (id > 0)
                            _specialCurrencyItems.Add(id);
                    }
                }
            }
            GatherBuddy.Log.Debug($"[MaterialSourceClassifier] Special currency set: {_specialCurrencyItems.Count} items");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[MaterialSourceClassifier] Special currency set failed: {ex.Message}");
        }
    }
}
