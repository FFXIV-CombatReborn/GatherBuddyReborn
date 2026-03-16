using System;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Vulcan;

namespace GatherBuddy.Crafting;

public static unsafe class SynthesisReader
{
    public static AtkUnitBase* GetSynthesisAddon()
    {
        var addon = Dalamud.GameGui.GetAddonByName("Synthesis");
        if (addon == null || addon.Address == nint.Zero)
            return null;
        
        return (AtkUnitBase*)addon.Address;
    }
    
    public static bool IsSynthesisWindowOpen()
    {
        var addon = GetSynthesisAddon();
        return addon != null && addon->IsVisible;
    }
    
    public static int GetStepIndex(AtkUnitBase* synthWindow)
    {
        if (synthWindow == null || synthWindow->AtkValuesCount < 16)
            return 0;
        return synthWindow->AtkValues[15].Int;
    }
    
    public static int GetProgress(AtkUnitBase* synthWindow)
    {
        if (synthWindow == null || synthWindow->AtkValuesCount < 6)
            return 0;
        return synthWindow->AtkValues[5].Int;
    }
    
    public static int GetQuality(AtkUnitBase* synthWindow)
    {
        if (synthWindow == null || synthWindow->AtkValuesCount < 10)
            return 0;
        return synthWindow->AtkValues[9].Int;
    }
    
    public static int GetDurability(AtkUnitBase* synthWindow)
    {
        if (synthWindow == null || synthWindow->AtkValuesCount < 8)
            return 0;
        return synthWindow->AtkValues[7].Int;
    }
    
    public static Condition GetCondition(AtkUnitBase* synthWindow)
    {
        if (synthWindow == null || synthWindow->AtkValuesCount < 13)
            return Condition.Normal;
        return (Condition)synthWindow->AtkValues[12].Int;
    }
    
    public static StepState? ReadCurrentStepState(CraftState craft, StepState? previousStep = null)
    {
        var synthWindow = GetSynthesisAddon();
        if (synthWindow == null)
            return null;
        
        var step = new StepState
        {
            Index = GetStepIndex(synthWindow),
            Progress = GetProgress(synthWindow),
            Quality = GetQuality(synthWindow),
            Durability = GetDurability(synthWindow),
            RemainingCP = (int)(Dalamud.ClientState.LocalPlayer?.CurrentCp ?? 0),
            Condition = GetCondition(synthWindow),
            TrainedPerfectionAvailable = previousStep?.TrainedPerfectionAvailable ?? true,
            HeartAndSoulAvailable = previousStep?.HeartAndSoulAvailable ?? craft.Specialist,
            QuickInnoAvailable = previousStep?.QuickInnoAvailable ?? false,
            MaterialMiracleCharges = previousStep?.MaterialMiracleCharges ?? (craft.MissionHasMaterialMiracle ? 1u : 0u),
            CarefulObservationLeft = previousStep?.CarefulObservationLeft ?? (craft.Specialist ? 2 : 0),
            PrevComboAction = previousStep?.PrevComboAction ?? VulcanSkill.None,
            PrevActionFailed = previousStep?.PrevActionFailed ?? false
        };
        
        ReadBuffsIntoStepState(step);
        
        if (step.TrainedPerfectionActive)
            step.TrainedPerfectionAvailable = false;
        
        return step;
    }
    
    private static void ReadBuffsIntoStepState(StepState step)
    {
        var player = Dalamud.ClientState.LocalPlayer;
        if (player == null)
            return;
        
        foreach (var status in player.StatusList)
        {
            switch (status.StatusId)
            {
                case 251:
                    step.IQStacks = status.Param;
                    break;
                case 252:
                    step.WasteNotLeft = status.Param;
                    break;
                case 2009:
                    step.WasteNotLeft = status.Param;
                    break;
                case 1164:
                    step.ManipulationLeft = status.Param;
                    break;
                case 254:
                    step.GreatStridesLeft = status.Param;
                    break;
                case 2189:
                    step.InnovationLeft = status.Param;
                    break;
                case 2226:
                    step.VenerationLeft = status.Param;
                    break;
                case 2191:
                    step.MuscleMemoryLeft = status.Param;
                    break;
                case 2190:
                    step.FinalAppraisalLeft = status.Param;
                    break;
                case 2665:
                    step.HeartAndSoulActive = true;
                    break;
                case 3858:
                    step.TrainedPerfectionActive = true;
                    break;
                case 3857:
                    step.ExpedienceLeft = status.Param;
                    break;
            }
        }
    }
}
