using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using System.Collections.Immutable;

namespace GatherBuddy.AutoGather.Extensions;

/// <summary>
/// Extension methods for the IGatherable interface.
/// </summary>
public static class GatherableExtensions
{
    private static readonly ImmutableArray<InventoryType> _inventoryTypes =
        [
            InventoryType.RetainerCrystals,
            InventoryType.RetainerPage1,
            InventoryType.RetainerPage2,
            InventoryType.RetainerPage3,
            InventoryType.RetainerPage4,
            InventoryType.RetainerPage5,
            InventoryType.RetainerPage6,
            InventoryType.RetainerPage7,
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
            InventoryType.Crystals
        ];
    private static readonly uint[] _inventoryTypesArray = [.. _inventoryTypes.Cast<uint>()];

    /// <summary>
    /// Gets the inventory count for a gatherable item.
    /// </summary>
    /// <param name="gatherable">The gatherable item to check.</param>
    /// <param name="checkRetainers">Check retainer inventory.</param>
    /// <returns>The count of the item in the inventory.</returns>
    public unsafe static int GetInventoryCount(this IGatherable gatherable)
    {
        if (GatherBuddy.Config.AutoGatherConfig.CheckRetainers && AllaganTools.Enabled)
        {
            return (int)AllaganTools.ItemCountOwned(gatherable.ItemId, true, _inventoryTypesArray);
        }

        var inventory = InventoryManager.Instance();
        
        if (gatherable.ItemData.IsCollectable)
        {
            var collectableCount = inventory->GetInventoryItemCount(gatherable.ItemId, false, false, false, 1);
            var normalCount = inventory->GetInventoryItemCount(gatherable.ItemId, false, false, false, 0);
            return collectableCount + normalCount;
        }
        
        return inventory->GetInventoryItemCount(gatherable.ItemId, false, false, false, 0);
    }
}
