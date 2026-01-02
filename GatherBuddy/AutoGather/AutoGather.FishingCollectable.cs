using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Text;
using Dalamud.Utility;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private static readonly List<string> CollectablePatterns =
        [
            "collectability of",
            "収集価値",
            "Sammlerwert",
            "Valeur de collection"
        ];

        private unsafe bool HandleFishingCollectable()
        {
            if (!GatherBuddy.Config.AutoGatherConfig.AutoCollectablesFishing)
            {
                GatherBuddy.Log.Debug("[AutoCollectable] Feature disabled in config");
                return false;
            }

            var addon = SelectYesnoAddon;
            if (addon == null)
                return false;
            
            if (!addon->IsReady)
            {
                GatherBuddy.Log.Debug("[AutoCollectable] SelectYesno addon not ready");
                return false;
            }
            
            GatherBuddy.Log.Debug("[AutoCollectable] SelectYesno addon found and ready");

            var master = new AddonMaster.SelectYesno(addon);
            var text = master.TextLegacy;
            GatherBuddy.Log.Debug($"[AutoCollectable] Read text: '{text}' (length={text.Length})");

            if (!CollectablePatterns.Any(text.Contains))
            {
                GatherBuddy.Log.Debug($"[AutoCollectable] Text does not match any collectable patterns");
                return false;
            }

            GatherBuddy.Log.Debug($"[AutoCollectable] Detected collectable dialog with text: {text}");

            if (addon->AtkValuesCount < 15)
            {
                GatherBuddy.Log.Debug($"[AutoCollectable] Not enough AtkValues ({addon->AtkValuesCount}), cannot read item ID");
                return false;
            }
            
            var itemIdEncoded = addon->AtkValues[14].UInt;
            if (itemIdEncoded < 500000)
            {
                GatherBuddy.Log.Debug($"[AutoCollectable] Invalid encoded item ID: {itemIdEncoded}");
                return false;
            }
            
            var itemId = itemIdEncoded - 500000;
            GatherBuddy.Log.Debug($"[AutoCollectable] Extracted item ID: {itemId} (from encoded value {itemIdEncoded})");

            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            var item = itemSheet.GetRowOrDefault(itemId);
            
            if (item == null || item.Value.RowId == 0)
            {
                GatherBuddy.Log.Debug($"[AutoCollectable] Failed to find item with ID {itemId}");
                return false;
            }
            
            var itemValue = item.Value;
            if (!itemValue.IsCollectable)
            {
                GatherBuddy.Log.Debug($"[AutoCollectable] Item [{itemValue.RowId}] {itemValue.Name} is not a collectable");
                return false;
            }

            GatherBuddy.Log.Debug($"[AutoCollectable] Detected item [{itemValue.RowId}] {itemValue.Name}");

            if (!int.TryParse(Regex.Match(text, @"\d+").Value, out var value))
            {
                GatherBuddy.Log.Debug($"[AutoCollectable] Failed to parse collectability value from text");
                return false;
            }

            GatherBuddy.Log.Debug($"[AutoCollectable] Detected collectability value: {value}");
            GatherBuddy.Log.Debug($"[AutoCollectable] Item data - AetherialReduce: {itemValue.AetherialReduce}, AdditionalData.RowId: {itemValue.AdditionalData.RowId}");
            {
                if (itemValue.AetherialReduce > 0)
                {
                    GatherBuddy.Log.Debug($"[AutoCollectable] Accepting [{itemValue.RowId}] {itemValue.Name} - aethersand fish");
                    Callback.Fire(&addon->AtkUnitBase, true, 0);
                    return true;
                }
                else if (itemValue.AdditionalData.RowId != 0)
                {
                    var wksItem = Dalamud.GameData.GetExcelSheet<WKSItemInfo>().GetRow(itemValue.AdditionalData.RowId);
                    if (wksItem.RowId != 0)
                {
                        GatherBuddy.Log.Debug($"[AutoCollectable] Accepting [{itemValue.RowId}] {itemValue.Name} - stellar fish for {wksItem.WKSItemSubCategory.ValueNullable?.Name ?? "null"}");
                        Callback.Fire(&addon->AtkUnitBase, true, 0);
                        return true;
                    }
                    else
                    {
                        GatherBuddy.Log.Debug($"[AutoCollectable] No CollectablesShopItem found for [{itemValue.RowId}] {itemValue.Name}");
                    }
                }
                else
                {
                    GatherBuddy.Log.Debug($"[AutoCollectable] No CollectablesShopItem found for [{itemValue.RowId}] {itemValue.Name}");
                }
            }

            GatherBuddy.Log.Debug($"[AutoCollectable] Accepting [{itemValue.RowId}] {itemValue.Name} - generic collectable fish with value {value}");
            Callback.Fire(&addon->AtkUnitBase, true, 0);
            return true;
        }
    }
}
