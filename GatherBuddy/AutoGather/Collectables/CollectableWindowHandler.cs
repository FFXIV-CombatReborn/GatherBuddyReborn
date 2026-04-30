using System;
using System.Text.RegularExpressions;
using Dalamud.Memory;
using GatherBuddy.Automation;
using Lumina.Excel.Sheets;
using GatherBuddy.Plugin;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace GatherBuddy.AutoGather.Collectables;

public unsafe class CollectableWindowHandler
{
    public unsafe bool IsReady => Automation.GenericHelpers.TryGetAddonByName<AtkUnitBase>("CollectablesShop", out var addon) &&
                                  Automation.GenericHelpers.IsAddonReady(addon);

    public unsafe void SelectJob(uint id)
    {
        if (Automation.GenericHelpers.TryGetAddonByName<AtkUnitBase>("CollectablesShop", out var addon) &&
            Automation.GenericHelpers.IsAddonReady(addon))
        {
            var selectJob = stackalloc AtkValue[]
            {
                new() {Type = ValueType.Int, Int = 14},
                new(){Type = ValueType.UInt, UInt = id }
            };
            addon->FireCallback(2, selectJob); 
        }
    }

    public unsafe void SelectItem(string itemName)
    {
        if (Automation.GenericHelpers.TryGetAddonByName<AtkUnitBase>("CollectablesShop", out var addon) &&
            Automation.GenericHelpers.IsAddonReady(addon))
        {
            var turnIn = new TurninWindow(addon);
            var index = turnIn.GetItemIndexOf(itemName);
            if (index == -1)
            {
                GatherBuddy.Log.Error($"[CollectableWindowHandler] Item '{itemName}' not found in current collectable tab");
                return;
            }
            GatherBuddy.Log.Debug($"[CollectableWindowHandler] Firing SelectItem callback with index {index}");
            var selectItem = stackalloc AtkValue[]
            {
                new() { Type = ValueType.Int, Int = 12 },
                new(){Type = ValueType.UInt, UInt = (uint)index}
            };
            addon->FireCallback(2, selectItem);
        }
    }

    public unsafe void SelectItemById(uint itemId)
    {
        var item = Dalamud.GameData.GetExcelSheet<Item>().GetRow(itemId);
        var itemName = item.Name.ToString();
        GatherBuddy.Log.Debug($"[CollectableWindowHandler] SelectItemById({itemId}) -> '{itemName}'");
        SelectItem(itemName);
    }
    
    public unsafe void SubmitItem()
    {
        if (Automation.GenericHelpers.TryGetAddonByName<AtkUnitBase>("CollectablesShop", out var addon) &&
            Automation.GenericHelpers.IsAddonReady(addon))
        {
            var submitItem = stackalloc AtkValue[]
            {
                new() { Type = ValueType.Int, Int = 15 },
                new(){Type = ValueType.UInt, UInt = 0}
            };
            addon->FireCallback(2, submitItem, true);
        }
    }
    
    public unsafe void CloseWindow()
    {
        if (Automation.GenericHelpers.TryGetAddonByName<AtkUnitBase>("CollectablesShop", out var addon) &&
            Automation.GenericHelpers.IsAddonReady(addon))
        {
            addon->Close(true);
        }
    }

    public unsafe int GetPurpleScripCount()
    {
        try
        {
            if (!Automation.GenericHelpers.TryGetAddonByName<AtkUnitBase>("CollectablesShop", out var addon) ||
                !Automation.GenericHelpers.IsAddonReady(addon))
                return -1;

            for (int i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var node = addon->UldManager.NodeList[i];
                if (node == null || node->Type != NodeType.Res || node->NodeId != 14) continue;

                var child = node->ChildNode;
                if (child == null) return -1;

                if (child->NodeId != 16) child = child->NextSiblingNode;
                if (child == null) return -1;

                var comp = child->GetAsAtkComponentNode();
                if (comp == null || comp->Component == null) return -1;

                var textNode = comp->Component->GetTextNodeById(4)->GetAsAtkTextNode();
                if (textNode == null) return -1;

                var raw = MemoryHelper.ReadSeStringNullTerminated((nint)textNode->NodeText.StringPtr.Value).TextValue;
                var left = raw?.Split('/')?[0];
                if (string.IsNullOrEmpty(left)) return -1;

                left = Regex.Replace(left, @"[^\d]", "");
                if (left.Length == 0) return -1;
                
                if (int.TryParse(left, out var val)) return val;
                return -1;
            }

            return -1;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CollectableWindowHandler] Error getting purple scrip count: {ex}");
            return -1;
        }
    }
}
