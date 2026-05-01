using System.Collections.Generic;
using System.Numerics;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.AutoGather.Collectables.Data;

namespace GatherBuddy.Config;

public class CollectableConfig
{
    public bool AutoTurnInCollectables { get; set; } = false;
    public bool BuyAfterEachCollect { get; set; } = false;
    public bool UseInventoryFullThreshold { get; set; } = false;
    public int CollectableInventoryThreshold { get; set; } = 100;
    public int InventoryFullThreshold { get; set; } = 140;
    public int ReserveScripAmount { get; set; } = 0;
    
    private CollectableShop? _preferredCollectableShop;
    
    public CollectableShop PreferredCollectableShop
    {
        get
        {
            var needsInit = _preferredCollectableShop == null || 
                           string.IsNullOrEmpty(_preferredCollectableShop.Name) ||
                           _preferredCollectableShop.Location == Vector3.Zero;
            
            if (needsInit)
            {
                _preferredCollectableShop = CollectableNpcLocations.GetDefaultShop();
            }
            return _preferredCollectableShop;
        }
        set => _preferredCollectableShop = value;
    }
    
    public List<ItemToPurchase> ScripShopItems { get; set; } = new();
}
