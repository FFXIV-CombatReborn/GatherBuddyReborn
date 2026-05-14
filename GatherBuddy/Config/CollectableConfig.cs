using System;
using GatherBuddy.AutoGather.Collectables.Data;

namespace GatherBuddy.Config;

public class CollectableConfig
{
    public bool AutoTurnInCollectables { get; set; } = false;
    public string AutoTurnInHardFailReason { get; set; } = string.Empty;
    public bool BuyAfterEachCollect { get; set; } = false;
    public int ReserveScripAmount { get; set; } = 0;
    public bool UseInventoryFullThreshold { get; set; } = false;
    public int CollectableInventoryThreshold { get; set; } = 100;
    public int InventoryFullThreshold { get; set; } = 140;
    public Guid GatheringPurchaseListId { get; set; } = Guid.Empty;
    public Guid CraftingPurchaseListId { get; set; } = Guid.Empty;
    public CollectableTurnInRoute? PreferredTurnInRoute { get; set; }
}
