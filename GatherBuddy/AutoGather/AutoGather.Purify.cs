using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Plugin;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Automation;
using GatherBuddy.AutoGather.Collectables;
using PurifyResult = GatherBuddy.Automation.AddonMaster.PurifyResult;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private bool HasReducibleItems()
        {
            if (!GatherBuddy.Config.AutoGatherConfig.DoReduce || Dalamud.Conditions[ConditionFlag.Mounted])
                return false;

            if (!QuestManager.IsQuestComplete(67633))
            {
                if (!_autoRetainerMultiModeEnabled && string.IsNullOrEmpty(_originalCharacterNameWorld))
                {
                    GatherBuddy.Config.AutoGatherConfig.DoReduce = false;
                    Communicator.PrintError(
                        "[GatherBuddyReborn] Aetherial reduction is enabled, but the relevant quest has not been completed yet. The feature has been disabled.");
                }
                GatherBuddy.Log.Debug($"[Reduction] Skipping reduction - quest not complete. AR MultiMode: {_autoRetainerMultiModeEnabled}, Original Character: {_originalCharacterNameWorld ?? "null"}");
                return false;
            }

            var items = ItemHelper.GetCurrentInventoryItems();
            foreach (var item in items)
            {
                if (!item.IsCollectable)
                    continue;

                // Check regular gatherables
                if (GatherBuddy.GameData.Gatherables.TryGetValue(item.BaseItemId, out var gatherable)
                 && gatherable.ItemData.AetherialReduce != 0)
                {
                    return true;
                }
                
                // Check fish
                if (GatherBuddy.GameData.Fishes.TryGetValue(item.BaseItemId, out var fish)
                 && fish.ItemData.AetherialReduce != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private unsafe void ReduceItems(bool reduceAll, Action? onComplete = null)
        {
            AutoStatus = "Aetherial reduction";
            var delay = (int)GatherBuddy.Config.AutoGatherConfig.ExecutionDelay;
            TaskManager.Enqueue(StopNavigation);
            if (PurifyItemSelectorAddon == null)
            {
                EnqueueActionWithDelay(() => { ActionManager.Instance()->UseAction(ActionType.GeneralAction, 21); });
                // Prevent the "Unable to execute command while occupied" message right after entering a house.
                TaskManager.DelayNext(500);
            }

            TaskManager.Enqueue(ReduceFirstItem,                                3000, true, "Reduce first item");
            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Occupied39], 5000, true, "Wait until first item reduction is complete");
            TaskManager.DelayNext(delay);
            TaskManager.Enqueue(StartAutoReduction,                             1000, true, "Start auto reduction");
            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Occupied39], 180000, true, "Wait until all items have been reduced");
            TaskManager.DelayNext(delay);
            TaskManager.Enqueue(() =>
            {
                EnqueueActionWithDelay(() =>
                {
                    if (PurifyResultAddon is var addon and not null)
                        Callback.Fire(addon, true, -1);
                });
                if (reduceAll && HasReducibleItems())
                    ReduceItems(true, onComplete);
                else
                {
                    EnqueueActionWithDelay(() =>
                    {
                        if (PurifyItemSelectorAddon is var addon and not null)
                            Callback.Fire(addon, true, -1);
                    });
                    if (onComplete != null)
                        TaskManager.Enqueue(() => onComplete());
                }
            });
        }

        private unsafe bool? ReduceFirstItem()
        {
            var addon = PurifyItemSelectorAddon;
            if (addon == null)
                return false;

            Callback.Fire(addon, true, 12, 0u);
            return true;
        }

        private unsafe bool? StartAutoReduction()
        {
            var addon = PurifyResultAddon;
            if (addon == null)
                return false;

            new PurifyResult(addon).Automatic();
            return true;
        }
    }
}
