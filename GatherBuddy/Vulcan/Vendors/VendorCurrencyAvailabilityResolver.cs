using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.AutoGather.Collectables;

namespace GatherBuddy.Vulcan.Vendors;

public enum VendorCurrencyAvailabilitySource
{
    Unknown,
    InventoryManagerGil,
    InventoryManagerGrandCompanySeals,
    InventoryManagerTomestones,
    InventoryManagerGoldSaucerCoin,
    InventoryManagerWolfMarks,
    InventoryManagerAlliedSeals,
    CurrencyManager,
    InventoryItemCount,
}

public readonly record struct VendorCurrencyAvailability(
    uint                             CurrencyItemId,
    string                           CurrencyName,
    uint                             AvailableAmount,
    VendorCurrencyAvailabilitySource Source
);

public static class VendorCurrencyAvailabilityResolver
{
    public static VendorCurrencyAvailability Resolve(VendorCurrencyGroup group, uint currencyItemId, string currencyName)
    {
        var normalizedCurrencyName = string.IsNullOrWhiteSpace(currencyName)
            ? currencyItemId == 0
                ? "currency"
                : $"currency {currencyItemId}"
            : currencyName;

        if (TryGetInventoryManagerAmount(group, currencyItemId, normalizedCurrencyName, out var availability))
            return availability;

        if (TryGetCurrencyManagerAmount(currencyItemId, normalizedCurrencyName, out availability))
            return availability;

        var inventoryAmount = currencyItemId == 0
            ? 0u
            : (uint)Math.Max(0, ItemHelper.GetInventoryAndArmoryItemCount(currencyItemId));
        return new VendorCurrencyAvailability(currencyItemId, normalizedCurrencyName, inventoryAmount, VendorCurrencyAvailabilitySource.InventoryItemCount);
    }

    private static unsafe bool TryGetInventoryManagerAmount(VendorCurrencyGroup group, uint currencyItemId, string currencyName,
        out VendorCurrencyAvailability availability)
    {
        availability = default;
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return false;

        if (group == VendorCurrencyGroup.Gil || currencyItemId == VendorShopResolver.GilCurrencyItemId)
        {
            availability = new VendorCurrencyAvailability(currencyItemId, currencyName, inventoryManager->GetGil(), VendorCurrencyAvailabilitySource.InventoryManagerGil);
            return true;
        }

        var isGrandCompanySealCurrency = VendorShopResolver.TryGetGrandCompanyIdFromSealCurrencyItemId(currencyItemId, out var grandCompanyId);
        if ((group == VendorCurrencyGroup.GrandCompanySeals || isGrandCompanySealCurrency)
         && isGrandCompanySealCurrency)
        {
            availability = new VendorCurrencyAvailability(currencyItemId, currencyName, inventoryManager->GetCompanySeals(grandCompanyId),
                VendorCurrencyAvailabilitySource.InventoryManagerGrandCompanySeals);
            return true;
        }

        if (group == VendorCurrencyGroup.Tomestones && currencyItemId != 0)
        {
            availability = new VendorCurrencyAvailability(currencyItemId, currencyName, inventoryManager->GetTomestoneCount(currencyItemId),
                VendorCurrencyAvailabilitySource.InventoryManagerTomestones);
            return true;
        }

        if (group == VendorCurrencyGroup.MGP || currencyItemId == VendorShopResolver.MgpCurrencyItemId)
        {
            availability = new VendorCurrencyAvailability(currencyItemId, currencyName, inventoryManager->GetGoldSaucerCoin(),
                VendorCurrencyAvailabilitySource.InventoryManagerGoldSaucerCoin);
            return true;
        }

        if (group == VendorCurrencyGroup.PvP || currencyItemId == VendorShopResolver.WolfMarkCurrencyItemId)
        {
            availability = new VendorCurrencyAvailability(currencyItemId, currencyName, inventoryManager->GetWolfMarks(),
                VendorCurrencyAvailabilitySource.InventoryManagerWolfMarks);
            return true;
        }

        if (currencyItemId == VendorShopResolver.AlliedSealCurrencyItemId)
        {
            availability = new VendorCurrencyAvailability(currencyItemId, currencyName, inventoryManager->GetAlliedSeals(),
                VendorCurrencyAvailabilitySource.InventoryManagerAlliedSeals);
            return true;
        }

        return false;
    }

    private static unsafe bool TryGetCurrencyManagerAmount(uint currencyItemId, string currencyName, out VendorCurrencyAvailability availability)
    {
        availability = default;
        if (currencyItemId == 0)
            return false;

        var currencyManager = CurrencyManager.Instance();
        if (currencyManager == null || !currencyManager->HasItem(currencyItemId))
            return false;

        availability = new VendorCurrencyAvailability(currencyItemId, currencyName, currencyManager->GetItemCount(currencyItemId),
            VendorCurrencyAvailabilitySource.CurrencyManager);
        return true;
    }

}
