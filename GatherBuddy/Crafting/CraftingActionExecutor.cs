using GatherBuddy.Vulcan;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Threading.Tasks;

namespace GatherBuddy.Crafting;

public class CraftingActionExecutor : IActionExecutor
{
    public bool CanExecuteAction(VulcanSkill action, CraftState craft, StepState step, string outReason = "")
    {
        if (action == VulcanSkill.None)
            return false;
        if (step.RemainingCP <= 0)
            return false;
        return true;
    }

    public unsafe Task<bool> TryExecuteActionAsync(VulcanSkill action)
    {
        try
        {
            var playerJobId = (uint)(Dalamud.ClientState.LocalPlayer?.ClassJob.RowId ?? 0);
            var actionId = action.ActionId(playerJobId);
            if (actionId == 0)
                actionId = (uint)action;
            if (actionId == 0)
            {
                GatherBuddy.Log.Warning($"[CraftingActionExecutor] Invalid action: {action}");
                return Task.FromResult(false);
            }

            var synthAddon = Dalamud.GameGui.GetAddonByName("Synthesis");
            if (synthAddon == null || synthAddon.Address == nint.Zero)
            {
                GatherBuddy.Log.Debug($"[CraftingActionExecutor] Synthesis addon not ready yet");
                return Task.FromResult(false);
            }

            var actionMgr = ActionManager.Instance();
            if (actionMgr == null)
            {
                GatherBuddy.Log.Error($"[CraftingActionExecutor] ActionManager not available");
                return Task.FromResult(false);
            }

            var actionType = actionId >= 100000 ? ActionType.CraftAction : ActionType.Action;
            var status = actionMgr->GetActionStatus(actionType, actionId);
            if (status != 0)
            {
                GatherBuddy.Log.Debug($"[CraftingActionExecutor] Cannot use action {action} ({actionId}): status={status}");
                return Task.FromResult(false);
            }

            GatherBuddy.Log.Debug($"[CraftingActionExecutor] Using action {action} ({actionId})");
            actionMgr->UseAction(actionType, actionId);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingActionExecutor] Error executing action: {ex.Message}");
            return Task.FromResult(false);
        }
    }
}
