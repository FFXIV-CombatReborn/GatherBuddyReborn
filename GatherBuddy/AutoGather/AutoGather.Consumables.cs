using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Helpers;
using GatherBuddy.Utilities;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        public static readonly Item[] PossibleCordials = FindItems(IsItemCordial);

        public static readonly Item[] PossibleFoods = FindItems(IsItemDoLFood);

        public static readonly Item[] PossiblePotions = FindItems(IsItemDoLPotion);

        public static readonly Item[] PossibleManuals = FindItems(IsItemDoLManual);

        private static readonly Dictionary<uint, uint> SquadronManualItemIdBuffId = new()
        {
            { 14949u, 1081u },
            { 14951u, 1083u },
            { 14952u, 1084u },
            { 14953u, 1085u },
            { 41707u, 1084u }
        };
        public static readonly Item[] PossibleSquadronManuals = FindItems(IsItemDoLSquadronManual);

        private static readonly Dictionary<uint, uint> SquadronPassItemIdBuffId = new()
        {
            { 14954u, 1061u }
        };
        public static readonly Item[] PossibleSquadronPasses = FindItems(IsItemDoLSquadronPass);


        public static unsafe int GetInventoryItemCount(uint itemRowId)
        {
            return InventoryManager.Instance()->GetInventoryItemCount(itemRowId < 1_000_000 ? itemRowId : itemRowId - 1_000_000, itemRowId >= 1_000_000);
        }

        private static uint[] GetItemFoodProps(Item item)
        {
            // Item UI category: 44 medicine, 46 meal
            if (item.ItemUICategory.RowId is not 44 and not 46
                || !item.ItemAction.IsValid
                // Buff: 48 well fed, 49 medicated
                || item.ItemAction.Value.Data[0] is not 48 and not 49)
            {
                return [];
            }

            var props = new List<uint>();
            foreach (var param in Dalamud.GameData.GetExcelSheet<ItemFood>().GetRow(item.ItemAction.Value.Data[1]).Params)
            {
                if (param.BaseParam.IsValid)
                {
                    props.Add(param.BaseParam.RowId);
                }
            }

            return [.. props];
        }

        private static Item[] FindItems(Predicate<Item> match)
        {
            if (Dalamud.GameData.GetExcelSheet<Item>() is not { } sheet)
            {
                return [];
            }

            var items = new List<Item>();
            foreach (var item in sheet)
            {
                if (match(item))
                {
                    items.Add(item);
                }
            }

            return [.. items];
        }

        private static bool IsItemCordial(Item item)
        {
            return item.RowId is 6141 or 12669 or 16911;
        }

        private static bool IsItemDoLFood(Item item)
        {
            if (item.RowId is 4751 or 4650 or 4728 or 4745)
            {
                return true;
            }

            if (item.ItemUICategory.RowId != 46)
            {
                return false;
            }
            // 10 GP, 71 gathering, 73 perception
            return Array.Exists(GetItemFoodProps(item), p => p is 10 or 72 or 73);
        }

        private static bool IsItemDoLPotion(Item item)
        {
            if (item.ItemUICategory.RowId != 44)
            {
                return false;
            }
            // 10 GP, 68 spiritbond, 69 durability, 72 gathering, 73 perception
            return Array.Exists(GetItemFoodProps(item), p => p is 10 or 68 or 69 or 72 or 73);
        }

        private static bool IsItemDoLManual(Item item)
        {
            return item.RowId is 12668 or 4633 or 4635 or 26553;
        }

        private static bool IsItemDoLSquadronManual(Item item)
        {
            return SquadronManualItemIdBuffId.ContainsKey(item.RowId);
        }

        private static bool IsItemDoLSquadronPass(Item item)
        {
            return SquadronPassItemIdBuffId.ContainsKey(item.RowId);
        }

        public unsafe bool IsCordialOnCooldown
        {
            get
            {
                var cordialRecastGroup = ActionManager.Instance()->GetRecastGroupDetail(68);
                return cordialRecastGroup->Total - cordialRecastGroup->Elapsed > 0;
            }
        }

        public static unsafe bool GetIsFoodBuffUp(uint itemId)
        {
            var buff = Player.FindStatus(48);
            if (buff == null)
            {
                return false;
            }
            else
            {
                var configuredItem = Array.Find(PossibleFoods, item => item.RowId == itemId || item.RowId + 1_000_000 == itemId);
                if (itemId > 1_000_000)
                {
                    return buff.Param == configuredItem.ItemAction.ValueNullable?.DataHQ[1] + 10000;
                }
                else
                {
                    return buff.Param == configuredItem.ItemAction.ValueNullable?.Data[1];
                }
            }
        }

        public static unsafe bool GetIsPotionBuffUp(uint itemId)
        {
            var buff = Player.FindStatus(49);
            if (buff == null)
            {
                return false;
            }
            else
            {
                var configuredItem = Array.Find(PossiblePotions, item => item.RowId == itemId || item.RowId + 1_000_000 == itemId);
                if (itemId > 1_000_000)
                {
                    return buff.Param == configuredItem.ItemAction.ValueNullable?.DataHQ[1] + 10000;
                }
                else
                {
                    return buff.Param == configuredItem.ItemAction.ValueNullable?.Data[1];
                }
            }
        }

        public static unsafe bool IsManualBuffUp
        {
            get
            {
                foreach (var status in Player.Status)
                {
                    if (status.StatusId == 46)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public static unsafe bool GetIsSquadronManualBuffUp(uint itemId)
        {
            if (SquadronManualItemIdBuffId.TryGetValue(itemId, out var requiredBuffId))
            {
                return Player.HasStatus(requiredBuffId);
            }
            else
            {
                return false;
            }
        }

        public static unsafe bool GetIsSquadronPassBuffUp(uint itemId)
        {
            if (SquadronPassItemIdBuffId.TryGetValue(itemId, out var requiredBuffId))
            {
                return Player.HasStatus(requiredBuffId);
            }
            else
            {
                return false;
            }
        }

        private unsafe void UseItem(uint itemId)
        {
            ActionManager.Instance()->UseAction(ActionType.Item, itemId, extraParam: 65535);
        }

        // Cordial, food and potion have no cast time and can be used while mounted
        private bool DoUseConsumablesWithoutCastTime(ConfigPreset config, bool skipThrottle = false)
        {
            if (Throttler.Throttle("DoUseConsumablesWithoutCastTime", 5000) || skipThrottle)
            {
                if (config.Consumables.Cordial.Enabled
                    && config.Consumables.Cordial.ItemId > 0
                    && !IsCordialOnCooldown
                    && Player.Object != null
                    && Player.Object.CurrentGp >= config.Consumables.Cordial.MinGP
                    && Player.Object.CurrentGp <= config.Consumables.Cordial.MaxGP
                    && Player.Object.CurrentGp < Player.Object.MaxGp
                    && GetInventoryItemCount(config.Consumables.Cordial.ItemId) > 0
                )
                {
                    EnqueueActionWithDelay(() => UseItem(config.Consumables.Cordial.ItemId));
                    return true;
                }

                if (config.Consumables.Food.Enabled
                    && config.Consumables.Food.ItemId > 0
                    && !GetIsFoodBuffUp(config.Consumables.Food.ItemId)
                    && GetInventoryItemCount(config.Consumables.Food.ItemId) > 0
                    )
                {
                    EnqueueActionWithDelay(() => UseItem(config.Consumables.Food.ItemId));
                    return true;
                }

                if (config.Consumables.Potion.Enabled
                    && config.Consumables.Potion.ItemId > 0
                    && !GetIsPotionBuffUp(config.Consumables.Potion.ItemId)
                    && GetInventoryItemCount(config.Consumables.Potion.ItemId) > 0
                    )
                {
                    EnqueueActionWithDelay(() => UseItem(config.Consumables.Potion.ItemId));
                    return true;
                }
            }
            return false;
        }

        // Manuals have cast time and cannot be used while mounted
        private uint GetConsumablesWithCastTime(ConfigPreset config)
        {
            if (config.Consumables.Manual.Enabled
                && config.Consumables.Manual.ItemId > 0
                && !IsManualBuffUp
                && GetInventoryItemCount(config.Consumables.Manual.ItemId) > 0
                )
            {
                return config.Consumables.Manual.ItemId;
            }

            if (config.Consumables.SquadronManual.Enabled
                && config.Consumables.SquadronManual.ItemId > 0
                && !GetIsSquadronManualBuffUp(config.Consumables.SquadronManual.ItemId)
                && GetInventoryItemCount(config.Consumables.SquadronManual.ItemId) > 0
                )
            {
                return config.Consumables.SquadronManual.ItemId;
            }

            if (config.Consumables.SquadronPass.Enabled
                && config.Consumables.SquadronPass.ItemId > 0
                && !GetIsSquadronPassBuffUp(config.Consumables.SquadronPass.ItemId)
                && GetInventoryItemCount(config.Consumables.SquadronPass.ItemId) > 0
                )
            {
                return config.Consumables.SquadronPass.ItemId;
            }
            return 0;
        }
    }
}
