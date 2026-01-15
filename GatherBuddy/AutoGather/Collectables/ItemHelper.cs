using Dalamud.Game.Inventory;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace GatherBuddy.AutoGather.Collectables;

public static class ItemHelper
{
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
}
