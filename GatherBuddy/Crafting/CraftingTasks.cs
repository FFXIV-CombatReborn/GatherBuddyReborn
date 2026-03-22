using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using Lumina.Excel.Sheets;
using System;
using System.Linq;

namespace GatherBuddy.Crafting;

public static class CraftingTasks
{
    private static RepairNPCNavigator? _navigator;
    private static bool _seenRepairConfirmation = false;
    private static bool _waitingForOccupied39 = false;
    private static DateTime _repairAutoStartTime = DateTime.MinValue;
    private static DateTime _repairCloseStartTime = DateTime.MinValue;
    private static bool _isSelfRepair = false;
    public enum TaskResult
    {
        Done,
        Retry,
        Abort
    }

    public static unsafe TaskResult TaskExitCraft()
    {
        switch (CraftingGameInterop.CurrentState)
        {
            case CraftingGameInterop.CraftState.WaitFinish:
            case CraftingGameInterop.CraftState.InProgress:
            case CraftingGameInterop.CraftState.WaitAction:
                return TaskResult.Retry;
            case CraftingGameInterop.CraftState.IdleNormal:
                return TaskResult.Done;
            case CraftingGameInterop.CraftState.IdleBetween:
                var addon = (AtkUnitBase*)Dalamud.GameGui.GetAddonByName("RecipeNote").Address;
                if (addon != null && addon->IsVisible)
                {
                    GatherBuddy.Log.Debug("[CraftingTasks] Closing recipe menu to exit crafting state via callback -1");
                    Callback.Fire(addon, true, -1);
                }
                var cosmicAddon = (AtkUnitBase*)Dalamud.GameGui.GetAddonByName("WKSRecipeNotebook").Address;
                if (cosmicAddon != null && cosmicAddon->IsVisible)
                {
                    GatherBuddy.Log.Debug("[CraftingTasks] Closing cosmic recipe menu to exit crafting state");
                    Callback.Fire(cosmicAddon, true, -1);
                }
                return TaskResult.Retry;
        }

        return TaskResult.Retry;
    }

    private static DateTime _nextRetry = DateTime.MinValue;

    public static unsafe TaskResult TaskOpenRepairWindow()
    {
        if (DateTime.Now < _nextRetry)
            return TaskResult.Retry;

        if (RepairManager.RepairWindowOpen())
        {
            GatherBuddy.Log.Debug("[CraftingTasks] Repair window already open");
            _nextRetry = DateTime.MinValue;
            return TaskResult.Done;
        }

        GatherBuddy.Log.Debug("[CraftingTasks] Opening repair window");
        ActionManager.Instance()->UseAction(ActionType.GeneralAction, 6);
        _nextRetry = DateTime.Now.AddMilliseconds(500);
        return TaskResult.Retry;
    }

    public static unsafe TaskResult TaskInteractWithRepairNPC()
    {
        if (DateTime.Now < _nextRetry)
            return TaskResult.Retry;

        if (!RepairManager.RepairNPCNearby(out var npc) || npc == null)
        {
            GatherBuddy.Log.Warning("[CraftingTasks] No repair NPC nearby");
            return TaskResult.Abort;
        }

        GatherBuddy.Log.Debug("[CraftingTasks] Interacting with repair NPC");
        TargetSystem.Instance()->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address);
        _nextRetry = DateTime.Now.AddMilliseconds(500);
        return TaskResult.Done;
    }
    
    public static unsafe TaskResult TaskSelectRepairFromMenu()
    {
        if (DateTime.Now < _nextRetry)
            return TaskResult.Retry;

        if (RepairManager.RepairWindowOpen())
        {
            GatherBuddy.Log.Debug("[CraftingTasks] Repair window opened");
            _nextRetry = DateTime.MinValue;
            return TaskResult.Done;
        }

        if (GenericHelpers.TryGetAddonByName<AddonSelectIconString>("SelectIconString", out var addonSelectIconString) && 
            addonSelectIconString->AtkUnitBase.IsVisible)
        {
            if (!RepairManager.RepairNPCNearby(out var npc) || npc == null)
            {
                GatherBuddy.Log.Warning("[CraftingTasks] Lost repair NPC");
                return TaskResult.Abort;
            }

            var enpcsheet = Dalamud.GameData.Excel.GetSheet<ENpcBase>();
            if (enpcsheet != null && enpcsheet.TryGetRow(npc.BaseId, out var enpc))
            {
                var repairDataList = enpc.ENpcData.ToList();
                var repairData = repairDataList.FirstOrDefault(x => x.RowId == 720915);
                var index = repairDataList.IndexOf(repairData);
                
                GatherBuddy.Log.Debug($"[CraftingTasks] Selecting repair option (index {index}) from menu");
                Callback.Fire(&addonSelectIconString->AtkUnitBase, true, index);
                _nextRetry = DateTime.Now.AddMilliseconds(500);
                return TaskResult.Retry;
            }
        }

        _nextRetry = DateTime.Now.AddMilliseconds(100);
        return TaskResult.Retry;
    }

    public static unsafe TaskResult TaskExecuteRepair(bool isSelfRepair = false)
    {
        if (DateTime.Now < _nextRetry)
            return TaskResult.Retry;

        if (!RepairManager.RepairWindowOpen())
        {
            GatherBuddy.Log.Warning("[CraftingTasks] Repair window not open, cannot execute repair");
            return TaskResult.Abort;
        }

        if (_waitingForOccupied39)
        {
            var occupied39 = Dalamud.Conditions[ConditionFlag.Occupied39];
            if (!occupied39)
            {
                GatherBuddy.Log.Debug("[CraftingTasks] Self-repair complete (Occupied39 cleared)");
                _waitingForOccupied39 = false;
                _seenRepairConfirmation = false;
                _isSelfRepair = false;
                _nextRetry = DateTime.MinValue;
                return TaskResult.Done;
            }
            GatherBuddy.Log.Debug($"[CraftingTasks] Waiting for self-repair to complete (Occupied39: {occupied39})");
            _nextRetry = DateTime.Now.AddMilliseconds(200);
            return TaskResult.Retry;
        }

        if (_seenRepairConfirmation && !GenericHelpers.TryGetAddonByName<AddonSelectYesno>("SelectYesno", out _))
        {
            if (_isSelfRepair)
            {
                GatherBuddy.Log.Debug("[CraftingTasks] SelectYesno disappeared, waiting for self-repair Occupied39");
                _waitingForOccupied39 = true;
                _nextRetry = DateTime.Now.AddMilliseconds(200);
                return TaskResult.Retry;
            }
            else
            {
                GatherBuddy.Log.Debug("[CraftingTasks] SelectYesno disappeared, NPC repair executing");
                _seenRepairConfirmation = false;
                _nextRetry = DateTime.MinValue;
                return TaskResult.Done;
            }
        }

        if (GenericHelpers.TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var addonSelectYesno) && 
            GenericHelpers.IsAddonReady(&addonSelectYesno->AtkUnitBase))
        {
            GatherBuddy.Log.Debug("[CraftingTasks] Clicking SelectYesno to confirm repair");
            RepairManager.ConfirmYesNo();
            _seenRepairConfirmation = true;
            _isSelfRepair = isSelfRepair;
            _nextRetry = DateTime.Now.AddMilliseconds(500);
            return TaskResult.Retry;
        }

        if (!_seenRepairConfirmation)
        {
            GatherBuddy.Log.Debug("[CraftingTasks] Clicking repair button");
            RepairManager.Repair();
            _nextRetry = DateTime.Now.AddMilliseconds(500);
            return TaskResult.Retry;
        }

        _nextRetry = DateTime.Now.AddMilliseconds(200);
        return TaskResult.Retry;
    }

    public static unsafe TaskResult TaskCloseRepairWindow()
    {
        if (DateTime.Now < _nextRetry)
            return TaskResult.Retry;

        if (!GenericHelpers.TryGetAddonByName<AddonRepair>("Repair", out var repairAddon) || !repairAddon->AtkUnitBase.IsVisible)
        {
            GatherBuddy.Log.Debug("[CraftingTasks] Repair window already closed");
            _nextRetry = DateTime.MinValue;
            _repairCloseStartTime = DateTime.MinValue;
            return TaskResult.Done;
        }

        if (_repairCloseStartTime == DateTime.MinValue)
            _repairCloseStartTime = DateTime.Now;

        if ((DateTime.Now - _repairCloseStartTime).TotalSeconds > 10)
        {
            GatherBuddy.Log.Warning("[CraftingTasks] Timed out closing repair window, forcing close via agent");
            AgentModule.Instance()->GetAgentByInternalId(AgentId.Repair)->Hide();
            _repairCloseStartTime = DateTime.MinValue;
            _nextRetry = DateTime.Now.AddMilliseconds(500);
            return TaskResult.Retry;
        }

        GatherBuddy.Log.Debug("[CraftingTasks] Closing repair window via callback");
        Callback.Fire(&repairAddon->AtkUnitBase, true, -1);
        _nextRetry = DateTime.Now.AddMilliseconds(500);
        return TaskResult.Retry;
    }
    
    public static unsafe TaskResult TaskWaitForRepairAutoClose()
    {
        if (DateTime.Now < _nextRetry)
            return TaskResult.Retry;

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RepairAuto", out var repairAutoAddon) && repairAutoAddon->IsVisible)
        {
            GatherBuddy.Log.Debug("[CraftingTasks] RepairAuto window still visible, waiting for repair to complete");
            _nextRetry = DateTime.Now.AddMilliseconds(200);
            return TaskResult.Retry;
        }

        GatherBuddy.Log.Debug("[CraftingTasks] RepairAuto window closed, repair complete");
        _nextRetry = DateTime.MinValue;
        return TaskResult.Done;
    }

    public static TaskResult TaskNavigateToRepairNPC(RepairNPCData targetNPC)
    {
        if (_navigator == null)
        {
            GatherBuddy.Log.Information($"[CraftingTasks] Starting navigation to repair NPC: {targetNPC.Name}");
            _navigator = new RepairNPCNavigator();
            _navigator.StartNavigation(targetNPC);
            return TaskResult.Retry;
        }

        _navigator.Update();

        if (_navigator.IsComplete)
        {
            if (_navigator.IsFailed)
            {
                GatherBuddy.Log.Error("[CraftingTasks] Navigation to repair NPC failed");
                _navigator.Stop();
                _navigator = null;
                return TaskResult.Abort;
            }

            GatherBuddy.Log.Information("[CraftingTasks] Navigation to repair NPC complete");
            _navigator = null;
            return TaskResult.Done;
        }

        return TaskResult.Retry;
    }

    public static void StopNavigation()
    {
        if (_navigator != null)
        {
            _navigator.Stop();
            _navigator = null;
        }
    }

    private static DateTime _materiaNextRetry = DateTime.MinValue;

    public static unsafe TaskResult TaskExtractAllMateria()
    {
        if (DateTime.Now < _materiaNextRetry)
            return TaskResult.Retry;

        if (!MateriaManager.IsExtractionUnlocked())
        {
            GatherBuddy.Log.Warning("[CraftingTasks] Materia extraction not unlocked, skipping");
            return TaskResult.Done;
        }

        if (!MateriaManager.HasFreeInventorySlots())
        {
            GatherBuddy.Log.Warning("[CraftingTasks] Inventory full, cannot extract materia");
            return TaskResult.Done;
        }

        if (!MateriaManager.IsSpiritbondReadyAny())
        {
            if (MateriaManager.IsMateriaMenuOpen())
            {
                GatherBuddy.Log.Debug("[CraftingTasks] All materia extracted, closing menu");
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 14);
                _materiaNextRetry = DateTime.Now.AddMilliseconds(500);
                return TaskResult.Retry;
            }
            GatherBuddy.Log.Debug("[CraftingTasks] Materia extraction complete");
            _materiaNextRetry = DateTime.MinValue;
            return TaskResult.Done;
        }

        if (!MateriaManager.IsMateriaMenuOpen())
        {
            GatherBuddy.Log.Debug("[CraftingTasks] Opening Materialize window");
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 14);
            _materiaNextRetry = DateTime.Now.AddMilliseconds(600);
            return TaskResult.Retry;
        }

        if (Dalamud.Conditions[ConditionFlag.Occupied39])
        {
            GatherBuddy.Log.Debug("[CraftingTasks] Waiting for materia extraction to finish (Occupied39)");
            _materiaNextRetry = DateTime.Now.AddMilliseconds(200);
            return TaskResult.Retry;
        }

        if (MateriaManager.IsMateriaDialogOpen())
        {
            GatherBuddy.Log.Debug("[CraftingTasks] Confirming MaterializeDialog");
            var dialogPtr = Dalamud.GameGui.GetAddonByName("MaterializeDialog");
            new AddonMaster.MaterializeDialog(dialogPtr.Address).Materialize();
            _materiaNextRetry = DateTime.Now.AddMilliseconds(500);
            return TaskResult.Retry;
        }

        GatherBuddy.Log.Debug($"[CraftingTasks] Triggering extraction for top-row item ({MateriaManager.ReadySpiritbondItemCount()} remaining)");
        var addonPtr = Dalamud.GameGui.GetAddonByName("Materialize");
        var atkUnit = (AtkUnitBase*)addonPtr.Address;
        Callback.Fire(atkUnit, true, 2, 0);
        _materiaNextRetry = DateTime.Now.AddMilliseconds(500);
        return TaskResult.Retry;
    }
}
