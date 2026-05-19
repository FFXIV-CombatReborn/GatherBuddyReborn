using System;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GatherBuddy.AutoGather.Collectables;

public unsafe class TurninWindow(AtkUnitBase* addon) : TreeListWindowBase(addon)
{
    protected override bool IsTargetNode(AtkResNode* node) => node->Type == (NodeType)1028 && node->NodeId == 28;

    protected override string ExtractLabel(AtkComponentTreeListItem* item)
    {
        var label = item->StringValues[0].Value;
        return SeString.Parse(label).TextValue;
    }

    public int GetItemIndexOf(string label)
    {
        GatherBuddy.Log.Debug($"[TurninWindow] GetItemIndexOf searching for '{label}' in {Labels.Length} items");
        
        int itemCount = 0;
        for (var i = 0; i < Labels.Length; i++)
        {
            var item = Items[i].Value;
            if (item == null)
                continue;
                
            var rawType = item->UIntValues.Count > 0 ? item->UIntValues[0] : 0;
            var itemType = (AtkComponentTreeListItemType)(rawType & 0xF);
            GatherBuddy.Log.Debug($"[TurninWindow] Index {i}: RawType=0x{rawType:X}, MaskedType={itemType}, Label='{Labels[i]}'");
            
            if (itemType == AtkComponentTreeListItemType.CollapsibleGroupHeader || 
                itemType == AtkComponentTreeListItemType.GroupHeader)
            {
                GatherBuddy.Log.Debug($"[TurninWindow] Skipping group header");
                continue;
            }
            
            if (Labels[i].Contains(label, StringComparison.OrdinalIgnoreCase))
            {
                GatherBuddy.Log.Debug($"[TurninWindow] Found match at item index {itemCount} (absolute index {i})");
                return itemCount;
            }
            
            itemCount++;
        }

        GatherBuddy.Log.Debug($"[TurninWindow] No match found for '{label}'");
        return -1;
    }
}
