using System;
using System.Linq;
using GatherBuddy.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace GatherBuddy.AutoGather.Collectables;

public unsafe class ScripShopWindowHandler
{
    private int _currentPage = -1;
    private int _currentSubPage = -1;
    
    public bool IsReady => Automation.GenericHelpers.TryGetAddonByName<AtkUnitBase>("InclusionShop", out var addon) &&
                          Automation.GenericHelpers.IsAddonReady(addon);
    
    public void OpenShop()
    {
        if (Automation.GenericHelpers.TryGetAddonByName("SelectIconString", out AtkUnitBase* addon))
        {
            var openShop = stackalloc AtkValue[]
            {
                new() { Type = ValueType.Int, Int = 0 }
            };
            addon->FireCallback(1, openShop);
        }
    }
    
    public void SelectPage(int page)
    {
        _currentPage = page;
        if (Automation.GenericHelpers.TryGetAddonByName("InclusionShop", out AtkUnitBase* addon))
        {
            var selectPage = stackalloc AtkValue[]
            {
                new() { Type = ValueType.Int, Int = 12 },
                new() { Type = ValueType.UInt, UInt = (uint)page }
            };
            
            for (int i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var node = addon->UldManager.NodeList[i];
                if (node->Type == (NodeType)1015 && node->NodeId == 7)
                {
                    var compNode = node->GetAsAtkComponentNode();
                    if (compNode == null || compNode->Component == null) continue;
                    
                    var dropDown = compNode->GetAsAtkComponentDropdownList();
                    dropDown->SelectItem(page);
                    addon->FireCallback(2, selectPage);
                }
            }
        }
    }
    
    public void SelectSubPage(int subPage)
    {
        _currentSubPage = subPage;
        if (Automation.GenericHelpers.TryGetAddonByName("InclusionShop", out AtkUnitBase* addon))
        {
            var selectSubPage = stackalloc AtkValue[]
            {
                new() { Type = ValueType.Int, Int = 13 },
                new() { Type = ValueType.UInt, UInt = (uint)subPage }
            };
            addon->FireCallback(2, selectSubPage);
        }
    }
    
    public bool SelectItem(uint itemId, int amount)
    {
        if (!Automation.GenericHelpers.TryGetAddonByName("InclusionShop", out AtkUnitBase* addon))
            return false;
        
        var shopItem = ScripShopItemManager.ShopItems.FirstOrDefault(x => x.ItemId == itemId);
        if (shopItem == null || shopItem.Page != _currentPage || shopItem.SubPage != _currentSubPage)
        {
            GatherBuddy.Log.Error($"[ScripShopWindowHandler] Item ID {itemId} not found in current page={_currentPage} subpage={_currentSubPage}");
            return false;
        }
        
        GatherBuddy.Log.Information($"[ScripShopWindowHandler] SelectItem: itemId={itemId}, index={shopItem.Index}, amount={amount}, page={_currentPage}, subpage={_currentSubPage}");
        var selectItem = stackalloc AtkValue[]
        {
            new() { Type = ValueType.Int, Int = 14 },
            new() { Type = ValueType.UInt, UInt = (uint)shopItem.Index },
            new() { Type = ValueType.UInt, UInt = (uint)amount }
        };
        addon->FireCallback(3, selectItem);
        return true;
    }
    
    public void PurchaseItem()
    {
        if (Automation.GenericHelpers.TryGetAddonByName("ShopExchangeItemDialog", out AtkUnitBase* addon))
        {
            var purchaseItem = stackalloc AtkValue[]
            {
                new() { Type = ValueType.Int, Int = 0 }
            };
            addon->FireCallback(1, purchaseItem);
            addon->Close(true);
        }
    }
    
    public int GetScripCount()
    {
        if (!Automation.GenericHelpers.TryGetAddonByName("InclusionShop", out AtkUnitBase* addon))
            return -1;
            
        for (int i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null) continue;
            if (node->Type == NodeType.Text && node->NodeId == 4)
            {
                var textNode = node->GetAsAtkTextNode();
                if (textNode != null)
                {
                    var text = textNode->NodeText.ToString().Replace(",", "");
                    if (int.TryParse(text, out var count))
                        return count;
                }
            }
        }
        return -1;
    }
    
    public void CloseShop()
    {
        if (Automation.GenericHelpers.TryGetAddonByName("InclusionShop", out AtkUnitBase* addon))
        {
            addon->Close(true);
        }
    }
}
