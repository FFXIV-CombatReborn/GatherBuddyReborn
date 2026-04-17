using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace GatherBuddy.AutoGather.Collectables;

public static class ItemHelper
{
    private static DateTime _lastInventoryManagerFallbackLogTime = DateTime.MinValue;
    public static List<GameInventoryItem> GetCurrentInventoryItems()
    {
        ReadOnlySpan<GameInventoryType> inventoriesToFetch = [
            GameInventoryType.Inventory1, GameInventoryType.Inventory2,
            GameInventoryType.Inventory3, GameInventoryType.Inventory4
        ];

        var inventoryItems = new List<GameInventoryItem>(140);
        for (int i = 0; i < inventoriesToFetch.Length; i++)
        {
            inventoryItems.AddRange(Dalamud.GameInventory.GetInventoryItems(inventoriesToFetch[i]));
        }
        return inventoryItems;
    }
    

    public static unsafe int GetInventoryAndArmoryItemCount(uint itemId, bool includeEquipped = false)
    {
        try
        {
            var inventoryManager = InventoryManager.Instance();
            if (inventoryManager != null)
            {
                var nq = inventoryManager->GetInventoryItemCount(itemId, false, includeEquipped, true);
                var hq = inventoryManager->GetInventoryItemCount(itemId, true, includeEquipped, true);
                return Math.Max(0, nq) + Math.Max(0, hq);
            }

            LogInventoryManagerFallback($"[ItemHelper] InventoryManager unavailable while counting item {itemId}, falling back to inventory bags only");
        }
        catch (Exception ex)
        {
            LogInventoryManagerFallback($"[ItemHelper] Failed to count item {itemId} with InventoryManager: {ex.Message}");
        }

        return GetCurrentInventoryItems()
            .Where(item => item.BaseItemId == itemId)
            .Sum(item => (int)item.Quantity);
    }
    public static List<Item> GetLuminaItemsFromInventory()
    {
        List<Item> luminaItems = new List<Item>();
        var inventoryItems = GetCurrentInventoryItems();
    
        foreach (var invItem in inventoryItems)
        {
            var luminaItem = Dalamud.GameData.GetExcelSheet<Item>().FirstOrDefault(i => i.RowId == invItem.BaseItemId);
            if (luminaItem.RowId != 0)
                luminaItems.Add(luminaItem);
        }
        return luminaItems;
    }

    private static void LogInventoryManagerFallback(string message)
    {
        if ((DateTime.UtcNow - _lastInventoryManagerFallbackLogTime).TotalSeconds < 5)
            return;

        _lastInventoryManagerFallbackLogTime = DateTime.UtcNow;
        GatherBuddy.Log.Debug(message);
    }
}
