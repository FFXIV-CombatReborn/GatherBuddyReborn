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
                GatherBuddy.Log.Debug($"[CollectableConfig] Initializing shop - was null: {_preferredCollectableShop == null}, name empty: {string.IsNullOrEmpty(_preferredCollectableShop?.Name)}, location zero: {_preferredCollectableShop?.Location == Vector3.Zero}");
                _preferredCollectableShop = CollectableNpcLocations.GetDefaultShop();
                GatherBuddy.Log.Debug($"[CollectableConfig] Set to: {_preferredCollectableShop.Name} at {_preferredCollectableShop.Location}");
            }
            return _preferredCollectableShop;
        }
        set => _preferredCollectableShop = value;
    }
    
    public List<ItemToPurchase> ScripShopItems { get; set; } = new();
}
