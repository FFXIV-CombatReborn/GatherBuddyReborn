using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using GatherBuddy.Crafting;
using GatherBuddy.Enums;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel.Sheets;
using GatheringType = GatherBuddy.Enums.GatheringType;

namespace GatherBuddy.Gui;

internal static class CraftingRowIcons
{
    internal readonly record struct RowIcon(uint IconId, string Tooltip);

    private static readonly Dictionary<(uint ItemId, bool IsPrecraft), IReadOnlyList<RowIcon>> _materialIconCache = new();
    private static readonly Dictionary<uint, RowIcon>                                          _crafterIconCache  = new();
    private static readonly Dictionary<uint, ushort>                                           _currencyIconCache = new();
    private static readonly Dictionary<uint, string>                                           _currencyNameCache = new();
    private static readonly Dictionary<uint, string>                                           _classJobNameCache = new();

    private const uint MinerClassJobId    = 16;
    private const uint BotanistClassJobId = 17;
    private const uint FisherClassJobId   = 18;

    private static bool IsElementalCrystal(uint itemId)
        => itemId is >= 2 and <= 19;

    private static IReadOnlyList<RowIcon> GetCrystalIcons()
    {
        var preferredJobId = GatherBuddy.Config.PreferredGatheringType.ToGroup() switch
        {
            GatheringType.Miner    => MinerClassJobId,
            GatheringType.Botanist => BotanistClassJobId,
            _                      => 0u,
        };
        if (preferredJobId == 0)
            return new List<RowIcon>(0);
        return new List<RowIcon>(1) { new(GetClassJobIconId(preferredJobId), GetClassJobName(preferredJobId)) };
    }

    public static IReadOnlyList<RowIcon> GetMaterialIcons(uint itemId, bool isPrecraft)
    {
        if (IsElementalCrystal(itemId))
            return GetCrystalIcons();

        var key = (itemId, isPrecraft);
        if (_materialIconCache.TryGetValue(key, out var cached))
            return cached;

        var icons = new List<RowIcon>();

        // 1. Gather class icon (Miner / Botanist / Fisher)
        if (TryGetGatherClassJobId(itemId, out var classJobId))
            icons.Add(new RowIcon(GetClassJobIconId(classJobId), GetClassJobName(classJobId)));

        // 2. Currency icon
        if (TryResolveCurrencyItemId(itemId, out var currencyItemId))
        {
            var currencyIcon = GetCurrencyIconId(currencyItemId);
            if (currencyIcon != 0)
                icons.Add(new RowIcon(currencyIcon, GetCurrencyName(currencyItemId)));
        }

        // 3. Crafter class icon (only on precraft material rows)
        if (isPrecraft && TryGetCrafterClassJobIdForItem(itemId, out var crafterClassJobId))
            icons.Add(new RowIcon(GetClassJobIconId(crafterClassJobId), GetClassJobName(crafterClassJobId)));

        IReadOnlyList<RowIcon> result = icons;
        _materialIconCache[key] = result;
        return result;
    }

    public static RowIcon GetCrafterIcon(Recipe recipe)
    {
        var craftType = recipe.CraftType.RowId;
        if (_crafterIconCache.TryGetValue(craftType, out var cached))
            return cached;

        var classJobId = craftType + 8;
        var icon = new RowIcon(GetClassJobIconId(classJobId), GetClassJobName(classJobId));
        _crafterIconCache[craftType] = icon;
        return icon;
    }

    public static void DrawIconsRightAligned(IReadOnlyList<RowIcon> icons, float iconSize = 16f)
    {
        if (icons.Count == 0)
            return;

        var size = new Vector2(iconSize, iconSize);
        for (var i = 0; i < icons.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, 4f);
            DrawIcon(icons[i], size);
        }
    }

    private static void DrawIcon(RowIcon icon, Vector2 size)
    {
        var texture = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(icon.IconId));
        if (texture.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, size);
        else
            ImGui.Dummy(size);

        if (!string.IsNullOrEmpty(icon.Tooltip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(icon.Tooltip);
    }

    private static bool TryGetGatherClassJobId(uint itemId, out uint classJobId)
    {
        if (GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var gatherable))
        {
            var group = gatherable.GatheringType.ToGroup();
            classJobId = group switch
            {
                GatheringType.Miner    => MinerClassJobId,
                GatheringType.Botanist => BotanistClassJobId,
                GatheringType.Fisher   => FisherClassJobId,
                _                      => 0,
            };
            if (classJobId != 0)
                return true;
        }

        if (GatherBuddy.GameData.Fishes.ContainsKey(itemId))
        {
            classJobId = FisherClassJobId;
            return true;
        }

        classJobId = 0;
        return false;
    }

    private static bool TryGetCrafterClassJobIdForItem(uint itemId, out uint classJobId)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (sheet != null)
        {
            foreach (var recipe in sheet)
            {
                if (recipe.ItemResult.RowId == itemId)
                {
                    classJobId = recipe.CraftType.RowId + 8;
                    return true;
                }
            }
        }

        classJobId = 0;
        return false;
    }

    private static bool TryResolveCurrencyItemId(uint itemId, out uint currencyItemId)
    {
        // Cheapest matching SpecialShop entry (covers tomestones, scrips, bicolor, hunt seals, MGP, wolf marks, etc.)
        currencyItemId = 0;
        var bestCost = uint.MaxValue;
        foreach (var entry in VendorShopResolver.SpecialShopEntries)
        {
            if (entry.ItemId != itemId || entry.Cost == 0)
                continue;
            if (entry.Cost < bestCost)
            {
                bestCost       = entry.Cost;
                currencyItemId = entry.CurrencyItemId;
            }
        }
        if (currencyItemId != 0)
            return true;

        // Grand Company seal exchange
        foreach (var entry in VendorShopResolver.GcShopEntries)
        {
            if (entry.ItemId == itemId && entry.CurrencyItemId != 0)
            {
                currencyItemId = entry.CurrencyItemId;
                return true;
            }
        }

        // Gil shop fallback
        foreach (var entry in VendorShopResolver.GilShopEntries)
        {
            if (entry.ItemId == itemId)
            {
                currencyItemId = VendorShopResolver.GilCurrencyItemId;
                return true;
            }
        }

        // Scrip fallback for items the vendor cache may not yet cover
        if (MaterialSourceClassifier.Classify(itemId) == MaterialSource.Scrip)
        {
            foreach (var shopItem in AutoGather.Collectables.ScripShopItemManager.ShopItems)
            {
                if (shopItem.ItemId == itemId)
                {
                    currencyItemId = shopItem.ScripType switch
                    {
                        AutoGather.Collectables.Data.ScripType.PurpleCraftersScrips  => 33913,
                        AutoGather.Collectables.Data.ScripType.PurpleGatherersScrips => 33914,
                        AutoGather.Collectables.Data.ScripType.OrangeCraftersScrips  => 41784,
                        AutoGather.Collectables.Data.ScripType.OrangeGatherersScrips => 41785,
                        _                                                            => 0u,
                    };
                    if (currencyItemId != 0)
                        return true;
                }
            }
        }

        return false;
    }

    private static uint GetClassJobIconId(uint classJobId)
        => classJobId == 0 ? 0u : 62100u + classJobId;

    private static ushort GetCurrencyIconId(uint currencyItemId)
    {
        if (_currencyIconCache.TryGetValue(currencyItemId, out var cached))
            return cached;

        var sheet = Dalamud.GameData.GetExcelSheet<Item>();
        ushort iconId = sheet != null && sheet.TryGetRow(currencyItemId, out var item)
            ? (ushort)item.Icon
            : (ushort)0;
        _currencyIconCache[currencyItemId] = iconId;
        return iconId;
    }

    private static string GetCurrencyName(uint currencyItemId)
    {
        if (_currencyNameCache.TryGetValue(currencyItemId, out var cached))
            return cached;

        var sheet = Dalamud.GameData.GetExcelSheet<Item>();
        var name = sheet != null && sheet.TryGetRow(currencyItemId, out var item)
            ? item.Name.ExtractText()
            : string.Empty;
        _currencyNameCache[currencyItemId] = name;
        return name;
    }

    private static string GetClassJobName(uint classJobId)
    {
        if (_classJobNameCache.TryGetValue(classJobId, out var cached))
            return cached;

        var sheet = Dalamud.GameData.GetExcelSheet<ClassJob>();
        var name = sheet != null && sheet.TryGetRow(classJobId, out var job)
            ? job.Name.ExtractText()
            : string.Empty;
        _classJobNameCache[classJobId] = name;
        return name;
    }
}
