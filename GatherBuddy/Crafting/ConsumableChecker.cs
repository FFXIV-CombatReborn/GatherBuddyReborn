using System;
using System.Linq;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace GatherBuddy.Crafting;

public static class ConsumableChecker
{
    public static unsafe bool UseItem(uint itemId)
    {
        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return false;
        
        return actionManager->UseAction(ActionType.Item, itemId, extraParam: 65535);
    }
    
    public static bool HasFoodBuff(uint foodItemId)
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return false;
        
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet == null || !itemSheet.TryGetRow(foodItemId, out var item))
            return false;
        
        if (!item.ItemAction.IsValid)
            return false;
        
        var action = item.ItemAction.Value;
        var nqParam = action.Data[1];
        var hqParam = action.DataHQ[1];
        
        foreach (var status in player.StatusList)
        {
            if (status.StatusId == 48 && status.RemainingTime > 10f)
            {
                if (status.Param == nqParam || status.Param == hqParam + 10000)
                    return true;
            }
        }
        
        return false;
    }
    
    public static bool HasMedicineBuff(uint medicineItemId)
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return false;
        
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet == null || !itemSheet.TryGetRow(medicineItemId, out var item))
            return false;
        
        if (!item.ItemAction.IsValid)
            return false;
        
        var action = item.ItemAction.Value;
        var nqParam = action.Data[1];
        var hqParam = action.DataHQ[1];
        
        foreach (var status in player.StatusList)
        {
            if (status.StatusId == 49 && status.RemainingTime > 10f)
            {
                if (status.Param == nqParam || status.Param == hqParam + 10000)
                    return true;
            }
        }
        
        return false;
    }
    
    public static bool HasManualBuff()
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return false;
        
        return player.StatusList.Any(s => s.StatusId == 45);
    }
    
    public static bool HasSquadronManualBuff()
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return false;
        
        uint[] squadronBuffs = { 1082, 1083, 1084, 1085 };
        return player.StatusList.Any(s => squadronBuffs.Contains(s.StatusId));
    }
    
    public static unsafe bool HasItemInInventory(uint itemId)
    {
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
            return false;
        
        return inventoryManager->GetInventoryItemCount(itemId) > 0;
    }
    
    public static bool ApplyConsumables(RecipeCraftSettings settings)
    {
        bool allApplied = true;
        
        if (settings.FoodItemId.HasValue)
        {
            if (!HasFoodBuff(settings.FoodItemId.Value))
            {
                uint itemToUse = settings.FoodHQ ? settings.FoodItemId.Value + 1_000_000 : settings.FoodItemId.Value;
                if (HasItemInInventory(itemToUse))
                {
                    GatherBuddy.Log.Debug($"[ConsumableChecker] Using food item {settings.FoodItemId.Value} ({(settings.FoodHQ ? "HQ" : "NQ")})");
                    UseItem(itemToUse);
                    allApplied = false;
                }
                else
                {
                    GatherBuddy.Log.Warning($"[ConsumableChecker] Food item {settings.FoodItemId.Value} ({(settings.FoodHQ ? "HQ" : "NQ")}) not found in inventory");
                }
            }
        }
        
        if (settings.MedicineItemId.HasValue)
        {
            if (!HasMedicineBuff(settings.MedicineItemId.Value))
            {
                uint itemToUse = settings.MedicineHQ ? settings.MedicineItemId.Value + 1_000_000 : settings.MedicineItemId.Value;
                if (HasItemInInventory(itemToUse))
                {
                    GatherBuddy.Log.Debug($"[ConsumableChecker] Using medicine item {settings.MedicineItemId.Value} ({(settings.MedicineHQ ? "HQ" : "NQ")})");
                    UseItem(itemToUse);
                    allApplied = false;
                }
                else
                {
                    GatherBuddy.Log.Warning($"[ConsumableChecker] Medicine item {settings.MedicineItemId.Value} ({(settings.MedicineHQ ? "HQ" : "NQ")}) not found in inventory");
                }
            }
        }
        
        if (settings.ManualItemId.HasValue)
        {
            if (!HasManualBuff())
            {
                if (HasItemInInventory(settings.ManualItemId.Value))
                {
                    GatherBuddy.Log.Debug($"[ConsumableChecker] Using manual item {settings.ManualItemId.Value}");
                    UseItem(settings.ManualItemId.Value);
                    allApplied = false;
                }
                else
                {
                    GatherBuddy.Log.Warning($"[ConsumableChecker] Manual item {settings.ManualItemId.Value} not found in inventory");
                }
            }
        }
        
        if (settings.SquadronManualItemId.HasValue)
        {
            if (!HasSquadronManualBuff())
            {
                if (HasItemInInventory(settings.SquadronManualItemId.Value))
                {
                    GatherBuddy.Log.Debug($"[ConsumableChecker] Using squadron manual item {settings.SquadronManualItemId.Value}");
                    UseItem(settings.SquadronManualItemId.Value);
                    allApplied = false;
                }
                else
                {
                    GatherBuddy.Log.Warning($"[ConsumableChecker] Squadron manual item {settings.SquadronManualItemId.Value} not found in inventory");
                }
            }
        }
        
        return allApplied;
    }
}
