using System;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Plugin;
using static ECommons.UIHelpers.AddonMasterImplementations.AddonMaster;

namespace GatherBuddy.AutoGather;

public partial class AutoGather
{

    unsafe int SpiritbondMax
    {
        get
        {
            if (!GatherBuddy.Config.AutoGatherConfig.DoMaterialize) return 0;

            var inventory = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
            var result    = 0;
            for (var slot = 0; slot < inventory->Size; slot++)
            {
                var inventoryItem = inventory->GetInventorySlot(slot);
                if (inventoryItem == null || inventoryItem->ItemId <= 0)
                    continue;

                //GatherBuddy.Log.Debug("Slot " + slot + " has " + inventoryItem->Spiritbond + " Spiritbond");
                if (inventoryItem->SpiritbondOrCollectability == 10000)
                {
                    result++;
                }
            }

            return result;
        }
    }

    private Random _rng = new();
    unsafe void DoMateriaExtraction()
    {
        if (!QuestManager.IsQuestComplete(66174))
        {
            GatherBuddy.Config.AutoGatherConfig.DoMaterialize = false;
            Communicator.PrintError("[GatherBuddy Reborn] Materia Extraction enabled but relevant quest not complete yet. Feature disabled.");
            return;
        }
        if (MaterializeAddon == null)
        {
            TaskManager.Enqueue(() => Navigation.StopNavigation());
            EnqueueActionWithDelay(() => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 14));
            TaskManager.Enqueue(() => MaterializeAddon != null);
            return;
        }

        TaskManager.Enqueue(YesAlready.Lock);
        EnqueueActionWithDelay(() => { if (MaterializeAddon is var addon and not null) Callback.Fire(&addon->AtkUnitBase, true, 2, 0); });
        TaskManager.Enqueue(() => MaterializeDialogAddon != null, 1000);
        EnqueueActionWithDelay(() => { if (MaterializeDialogAddon is var addon and not null) new MaterializeDialog(addon).Materialize(); });
        TaskManager.Enqueue(() => !Svc.Condition[ConditionFlag.Occupied39]);
        TaskManager.DelayNext(_rng.Next(500, 2000));

        if (SpiritbondMax == 1) 
        {
            EnqueueActionWithDelay(() => { if (MaterializeAddon is var addon and not null) Callback.Fire(&addon->AtkUnitBase, true, -1); });
            TaskManager.Enqueue(YesAlready.Unlock);
        }
    }
}
