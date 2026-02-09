using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using GatherBuddy.Utilities;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        public static readonly Item[] PossibleCordials = Dalamud.GameData.GetExcelSheet<Item>()?.Where(IsItemCordial).ToArray() ?? [];

        public static readonly Item[] PossibleFoods = Dalamud.GameData.GetExcelSheet<Item>()?.Where(IsItemDoLFood).ToArray() ?? [];

        public static readonly Item[] PossiblePotions = Dalamud.GameData.GetExcelSheet<Item>()?.Where(IsItemDoLPotion).ToArray() ?? [];

        public static readonly Item[] PossibleManuals = Dalamud.GameData.GetExcelSheet<Item>()?.Where(IsItemDoLManual).ToArray() ?? [];

        private static readonly Dictionary<uint, uint> SquadronManualItemIdBuffId = new ()
        {
            { 14949u, 1081u },
            { 14951u, 1083u },
            { 14952u, 1084u },
            { 14953u, 1085u },
            { 41707u, 1084u }
        };
        public static readonly Item[] PossibleSquadronManuals = Dalamud.GameData.GetExcelSheet<Item>()?.Where(IsItemDoLSquadronManual).ToArray() ?? [];

        private static readonly Dictionary<uint, uint> SquadronPassItemIdBuffId = new ()
        {
            { 14954u, 1061u }
        };
        public static readonly Item[] PossibleSquadronPasses = Dalamud.GameData.GetExcelSheet<Item>()?.Where(IsItemDoLSquadronPass).ToArray() ?? [];


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
                return [];
            return Dalamud.GameData.GetExcelSheet<ItemFood>().GetRow(item.ItemAction.Value.Data[1]).Params.Where(v => v.BaseParam.IsValid).Select(v => v.BaseParam.RowId).ToArray() ?? [];
        }

        private static bool IsItemCordial(Item item)
        {
            return item.RowId is 6141 or 12669 or 16911;
        }

        private static bool IsItemDoLFood(Item item)
        {
            if (item.RowId is 4751 or 4650 or 4728 or 4745)
                return true;
                
            if (item.ItemUICategory.RowId != 46)
                return false;
            // 10 GP, 71 gathering, 73 perception
            return GetItemFoodProps(item).Any(p => p is 10 or 72 or 73);
        }

        private static bool IsItemDoLPotion(Item item)
        {
            if (item.ItemUICategory.RowId != 44)
                return false;
            // 10 GP, 68 spiritbond, 69 durability, 72 gathering, 73 perception
            return GetItemFoodProps(item).Any(p => p is 10 or 68 or 69 or 72 or 73);
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

        public unsafe bool GetIsFoodBuffUp(uint itemId)
        {
            var buff = Player.Status.FirstOrDefault(s => s.StatusId == 48);
            if (buff == null)
            {
                return false;
            }
            else
            {
                var configuredItem = PossibleFoods.FirstOrDefault(item => new[] { item.RowId, item.RowId + 1_000_000 }.Contains(itemId));
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

        public unsafe bool GetIsPotionBuffUp(uint itemId)
        {
            var buff = Player.Status.FirstOrDefault(s => s.StatusId == 49);
            if (buff == null)
            {
                return false;
            }
            else
            {
                var configuredItem = PossiblePotions.FirstOrDefault(item => new[] { item.RowId, item.RowId + 1_000_000 }.Contains(itemId));
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

        public unsafe bool IsManualBuffUp => Player.Status.Any(s => s.StatusId == 46);

        public unsafe bool GetIsSquadronManualBuffUp(uint itemId)
        {
            if (SquadronManualItemIdBuffId.TryGetValue(itemId, out var requiredBuffId))
            {
                return Player.Status.Any(s => s.StatusId == requiredBuffId);
            }
            else
            {
                return false;
            }
        }

        public unsafe bool GetIsSquadronPassBuffUp(uint itemId)
        {
            if (SquadronPassItemIdBuffId.TryGetValue(itemId, out var requiredBuffId))
            {
                return Player.Status.Any(s => s.StatusId == requiredBuffId);
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
