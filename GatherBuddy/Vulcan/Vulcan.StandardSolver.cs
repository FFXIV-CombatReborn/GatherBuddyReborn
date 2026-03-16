using System;
using System.Collections.Generic;

namespace GatherBuddy.Vulcan;

public class StandardSolverConfig
{
    public bool UseTricksGood { get; set; } = true;
    public bool UseTricksExcellent { get; set; } = false;
    public int MaxPercentage { get; set; } = 100;
    public bool UseQualityStarter { get; set; } = false;
    public int SolverCollectibleMode { get; set; } = 3;
    public int MaxIQPrepTouch { get; set; } = 10;
    public bool UseSpecialist { get; set; } = false;
    public bool UseMaterialMiracle { get; set; } = false;
    public int MinimumStepsBeforeMiracle { get; set; } = 2;
    public bool MaterialMiracleMulti { get; set; } = false;
}

public class StandardSolverDefinition : ISolverDefinition
{
    public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
    {
        if (!craft.CraftExpert && (craft.CraftHQ || craft.CraftRequiredQuality > 0))
            yield return new(this, 0, 2, "Standard Recipe Solver");
    }

    public Solver Create(CraftState craft, int flavor) => new StandardSolver(GatherBuddy.Config.StandardSolverConfig);
}

public class StandardSolver : Solver
{
    private bool _manipulationUsed;
    private bool _wasteNotUsed;
    private bool _qualityStarted;
    private bool _venerationUsed;
    private bool _trainedEyeUsed;
    private bool _materialMiracleUsed;

    private Solver? _fallback;
    private StandardSolverConfig _config;

    public StandardSolver(StandardSolverConfig config)
    {
        _config = config;
        _fallback = new ProgressOnlySolver();
    }

    public override Recommendation Solve(CraftState craft, StepState step)
    {
        var rec = GetRecommendation(craft, step);

        if (Simulator.GetDurabilityCost(step, rec.Action) == 0)
        {
            if (rec.Action != VulcanSkill.MaterialMiracle)
            {
                if (step.Durability <= 10 && Simulator.CanUseAction(craft, step, VulcanSkill.MastersMend))
                    rec.Action = VulcanSkill.MastersMend;
                if (step.Durability <= 10 && Simulator.CanUseAction(craft, step, VulcanSkill.Manipulation) && step.ManipulationLeft <= 1)
                    rec.Action = VulcanSkill.Manipulation;
                if (step.Durability <= 10 && Simulator.CanUseAction(craft, step, VulcanSkill.ImmaculateMend) && craft.CraftDurability >= 70)
                    rec.Action = VulcanSkill.ImmaculateMend;
            }
        }
        else
        {
            var stepClone = rec.Action;
            if (WillActFail(craft, step, stepClone) && Simulator.CanUseAction(craft, step, VulcanSkill.MastersMend))
                rec.Action = VulcanSkill.MastersMend;
            if (WillActFail(craft, step, stepClone) && Simulator.CanUseAction(craft, step, VulcanSkill.Manipulation) && step.ManipulationLeft <= 1)
                rec.Action = VulcanSkill.Manipulation;
            if (WillActFail(craft, step, stepClone) && Simulator.CanUseAction(craft, step, VulcanSkill.ImmaculateMend) && craft.CraftDurability >= 70)
                rec.Action = VulcanSkill.ImmaculateMend;
        }

        if ((rec.Action is not VulcanSkill.MastersMend or VulcanSkill.ImmaculateMend) &&
            step.Quality < craft.CraftQualityMax &&
            Simulator.CanUseAction(craft, step, VulcanSkill.ByregotsBlessing) &&
            step.RemainingCP - Simulator.GetCPCost(step, rec.Action) < Simulator.GetCPCost(step, VulcanSkill.ByregotsBlessing) &&
            !WillActFail(craft, step, VulcanSkill.ByregotsBlessing))
        {
            rec.Action = VulcanSkill.ByregotsBlessing;
        }

        if ((rec.Action is VulcanSkill.MastersMend or VulcanSkill.ImmaculateMend) &&
            step.Condition is Condition.Good or Condition.Excellent && Simulator.CanUseAction(craft, step, VulcanSkill.TricksOfTrade))
            rec.Action = VulcanSkill.TricksOfTrade;

        if (Simulator.GetDurabilityCost(step, rec.Action) == 20 && !_trainedEyeUsed && step.TrainedPerfectionAvailable && step.VenerationLeft == 0)
            rec.Action = VulcanSkill.TrainedPerfection;

        if (WillActFail(craft, step, rec.Action))
        {
            var bestSynth = BestSynthesis(craft, step);
            rec.Action = bestSynth != VulcanSkill.BasicSynthesis ? bestSynth :
                CanSpamBasicToComplete(craft, step) ? VulcanSkill.BasicSynthesis : VulcanSkill.RapidSynthesis;
        }

        return rec;
    }

    private static bool InTouchRotation(CraftState craft, StepState step)
        => step.PrevComboAction == VulcanSkill.BasicTouch && craft.StatLevel >= Simulator.MinLevel(VulcanSkill.StandardTouch) || step.PrevComboAction == VulcanSkill.StandardTouch && craft.StatLevel >= Simulator.MinLevel(VulcanSkill.AdvancedTouch);

    public VulcanSkill BestSynthesis(CraftState craft, StepState step, bool progOnly = false)
    {
        var remainingProgress = craft.CraftProgress - step.Progress;
        if (CalculateNewProgress(craft, step, VulcanSkill.BasicSynthesis) >= craft.CraftProgress)
        {
            return VulcanSkill.BasicSynthesis;
        }

        if (Simulator.CanUseAction(craft, step, VulcanSkill.IntensiveSynthesis))
        {
            return VulcanSkill.IntensiveSynthesis;
        }

        if (!_qualityStarted && !progOnly)
        {
            if (CalculateNewProgress(craft, step, VulcanSkill.BasicSynthesis) >= craft.CraftProgress - Simulator.BaseProgress(craft))
                return VulcanSkill.BasicSynthesis;
        }

        if (Simulator.CanUseAction(craft, step, VulcanSkill.Groundwork) && step.Durability > Simulator.GetDurabilityCost(step, VulcanSkill.Groundwork))
        {
            return VulcanSkill.Groundwork;
        }

        if (Simulator.CanUseAction(craft, step, VulcanSkill.PrudentSynthesis))
        {
            return VulcanSkill.PrudentSynthesis;
        }

        if (Simulator.CanUseAction(craft, step, VulcanSkill.CarefulSynthesis))
        {
            return VulcanSkill.CarefulSynthesis;
        }

        if (CanSpamBasicToComplete(craft, step))
        {
            return VulcanSkill.BasicSynthesis;
        }

        return VulcanSkill.BasicSynthesis;
    }

    private static bool CanSpamBasicToComplete(CraftState craft, StepState step)
    {
        while (true)
        {
            var (res, next) = Simulator.Execute(craft, step, VulcanSkill.BasicSynthesis, 0, 1);
            if (res == Simulator.ExecuteResult.CantUse)
                return false;
            step = next;
            if (step.Progress >= craft.CraftProgress)
                return true;
        }
    }

    public Recommendation GetRecommendation(CraftState craft, StepState step)
    {
        var fallbackRec = _fallback!.Solve(craft, step);

        _manipulationUsed |= step.PrevComboAction == VulcanSkill.Manipulation;
        _trainedEyeUsed |= step.PrevComboAction == VulcanSkill.TrainedEye;
        _wasteNotUsed |= step.PrevComboAction is VulcanSkill.WasteNot or VulcanSkill.WasteNot2;
        _qualityStarted |= step.PrevComboAction is VulcanSkill.BasicTouch or VulcanSkill.StandardTouch or VulcanSkill.AdvancedTouch or VulcanSkill.HastyTouch or VulcanSkill.ByregotsBlessing or VulcanSkill.PrudentTouch
            or VulcanSkill.PreciseTouch or VulcanSkill.TrainedEye or VulcanSkill.PreparatoryTouch or VulcanSkill.TrainedFinesse or VulcanSkill.Innovation;
        _venerationUsed |= step.PrevComboAction == VulcanSkill.Veneration;
        _materialMiracleUsed |= step.PrevComboAction == VulcanSkill.MaterialMiracle && !_config.MaterialMiracleMulti;

        if (step.MaterialMiracleActive)
            return fallbackRec;

        if (_config.UseMaterialMiracle && step.Index >= _config.MinimumStepsBeforeMiracle && !_materialMiracleUsed && Simulator.CanUseAction(craft, step, VulcanSkill.MaterialMiracle))
            return new(VulcanSkill.MaterialMiracle);

        bool inCombo = (step.PrevComboAction == VulcanSkill.BasicTouch && Simulator.CanUseAction(craft, step, VulcanSkill.StandardTouch)) || (step.PrevComboAction == VulcanSkill.StandardTouch && Simulator.CanUseAction(craft, step, VulcanSkill.AdvancedTouch));
        var act = BestSynthesis(craft, step);
        var goingForQuality = GoingForQuality(craft, step, out var maxQuality);

        if (step.Index == 1 && CanFinishCraft(craft, step, VulcanSkill.DelicateSynthesis) && CalculateNewQuality(craft, step, VulcanSkill.DelicateSynthesis) >= maxQuality && Simulator.CanUseAction(craft, step, VulcanSkill.DelicateSynthesis)) return new(VulcanSkill.DelicateSynthesis);
        if (!goingForQuality && CanFinishCraft(craft, step, act)) return new(act);

        if (Simulator.CanUseAction(craft, step, VulcanSkill.TrainedEye) && goingForQuality) return new(VulcanSkill.TrainedEye);
        if (Simulator.CanUseAction(craft, step, VulcanSkill.TricksOfTrade))
        {
            if (step.Index > 2 && (step.Condition == Condition.Good && _config.UseTricksGood || step.Condition == Condition.Excellent && _config.UseTricksExcellent))
                return new(VulcanSkill.TricksOfTrade);

            if (step.RemainingCP < 7 ||
                craft.StatLevel < Simulator.MinLevel(VulcanSkill.PreciseTouch) && step.Condition == Condition.Good && step.InnovationLeft == 0 && step.WasteNotLeft == 0 && !InTouchRotation(craft, step))
                return new(VulcanSkill.TricksOfTrade);
        }

        if ((maxQuality == 0 || _config.MaxPercentage == 0) && !craft.CraftCollectible)
        {
            if (step.Index == 1 && Simulator.CanUseAction(craft, step, VulcanSkill.MuscleMemory)) return new(VulcanSkill.MuscleMemory);
            if (CanFinishCraft(craft, step, act)) return new(act);
            return new(act);
        }

        if (goingForQuality)
        {
            if (!_config.UseQualityStarter && craft.StatLevel >= Simulator.MinLevel(VulcanSkill.MuscleMemory))
            {
                if (Simulator.CanUseAction(craft, step, VulcanSkill.MuscleMemory) && !CanFinishCraft(craft, step, VulcanSkill.MuscleMemory)) return new(VulcanSkill.MuscleMemory);

                if (step.MuscleMemoryLeft > 0 && !CanFinishCraft(craft, step, VulcanSkill.BasicSynthesis))
                {
                    if (craft.StatLevel < Simulator.MinLevel(VulcanSkill.IntensiveSynthesis) && step.Condition is Condition.Good or Condition.Excellent && Simulator.CanUseAction(craft, step, VulcanSkill.PreciseTouch)) return new(VulcanSkill.PreciseTouch);
                    if (Simulator.CanUseAction(craft, step, VulcanSkill.FinalAppraisal) && step.FinalAppraisalLeft == 0 && CanFinishCraft(craft, step, act)) return new(VulcanSkill.FinalAppraisal);
                    return new(act);
                }
            }

            if (_config.UseQualityStarter)
            {
                if (Simulator.CanUseAction(craft, step, VulcanSkill.Reflect)) return new(VulcanSkill.Reflect);
            }

            if (Simulator.CanUseAction(craft, step, VulcanSkill.BasicTouch) && CalculateNewQuality(craft, step, VulcanSkill.BasicTouch) >= craft.CraftQualityMax && step.Index == 1)
                return new(VulcanSkill.BasicTouch);

            if (Simulator.CanUseAction(craft, step, VulcanSkill.Manipulation) && step.ManipulationLeft == 0) return new(VulcanSkill.Manipulation);

            if (step.Progress < craft.CraftProgress - 1 && (!_qualityStarted || !Simulator.CanUseAction(craft, step, VulcanSkill.FinalAppraisal)))
            {
                bool canUseAct = step.Progress + Simulator.BaseProgress(craft) < craft.CraftProgress;
                if (canUseAct)
                {
                    bool shouldUseVeneration = CheckIfVenerationIsWorth(craft, step, act);

                    if (Simulator.CanUseAction(craft, step, VulcanSkill.Veneration) && step.VenerationLeft == 0 && shouldUseVeneration) return new(VulcanSkill.Veneration);
                    if (Simulator.CanUseAction(craft, step, VulcanSkill.WasteNot2) && step.WasteNotLeft == 0 && !_wasteNotUsed) return new(VulcanSkill.WasteNot2);
                    if (Simulator.CanUseAction(craft, step, VulcanSkill.WasteNot) && step.WasteNotLeft == 0 && !_wasteNotUsed) return new(VulcanSkill.WasteNot);
                    if (Simulator.CanUseAction(craft, step, VulcanSkill.FinalAppraisal) && step.FinalAppraisalLeft == 0 && CanFinishCraft(craft, step, act)) return new(VulcanSkill.FinalAppraisal, $"Synth is {act}");
                    if (!CanFinishCraft(craft, step, act))
                        return new(act);
                }
            }

            if (Simulator.CanUseAction(craft, step, VulcanSkill.ByregotsBlessing) && !WillActFail(craft, step, VulcanSkill.ByregotsBlessing))
            {
                var newQuality = CalculateNewQuality(craft, step, VulcanSkill.ByregotsBlessing);
                var newHQPercent = maxQuality > 0 ? Calculations.GetHQChance(newQuality * 100.0 / maxQuality) : 100;
                var newDone = craft.CraftQualityMin1 == 0 ? newHQPercent >= _config.MaxPercentage : newQuality >= maxQuality;
                if (newDone) return new(VulcanSkill.ByregotsBlessing);
            }

            if (_wasteNotUsed && Simulator.CanUseAction(craft, step, VulcanSkill.PreciseTouch) && step.GreatStridesLeft == 0 && step.Condition is Condition.Good or Condition.Excellent && !WillActFail(craft, step, VulcanSkill.PreciseTouch)) return new(VulcanSkill.PreciseTouch);
            if (craft.StatLevel < Simulator.MinLevel(VulcanSkill.PreciseTouch) && step.GreatStridesLeft == 0 && step.Condition is Condition.Excellent)
            {
                if (step.PrevComboAction == VulcanSkill.BasicTouch && Simulator.CanUseAction(craft, step, VulcanSkill.StandardTouch) && step.Durability - Simulator.GetDurabilityCost(step, VulcanSkill.StandardTouch) > 0) return new(VulcanSkill.StandardTouch);
                if (Simulator.CanUseAction(craft, step, VulcanSkill.BasicTouch) && step.Durability - Simulator.GetDurabilityCost(step, VulcanSkill.BasicTouch) > 0) return new(VulcanSkill.BasicTouch);
                if (Simulator.CanUseAction(craft, step, VulcanSkill.TricksOfTrade)) return new(VulcanSkill.TricksOfTrade);
            }
            if (step.InnovationLeft == 0 && Simulator.CanUseAction(craft, step, VulcanSkill.Innovation) && !inCombo && step.RemainingCP >= 36) return new(VulcanSkill.Innovation);
            if (!_wasteNotUsed && step.WasteNotLeft == 0 && Simulator.CanUseAction(craft, step, VulcanSkill.WasteNot2)) return new(VulcanSkill.WasteNot2);
            if (!_wasteNotUsed && step.WasteNotLeft == 0 && Simulator.CanUseAction(craft, step, VulcanSkill.WasteNot) && craft.StatLevel < Simulator.MinLevel(VulcanSkill.WasteNot2)) return new(VulcanSkill.WasteNot);
            if (Simulator.CanUseAction(craft, step, VulcanSkill.PrudentTouch) && step.Durability == 10) return new(VulcanSkill.PrudentTouch);
            if (step.GreatStridesLeft == 0 && Simulator.CanUseAction(craft, step, VulcanSkill.GreatStrides) && step.Condition != Condition.Excellent && step.RemainingCP >= Simulator.GetCPCost(step, VulcanSkill.GreatStrides) + Simulator.GetCPCost(step, VulcanSkill.ByregotsBlessing) && !WillActFail(craft, step, VulcanSkill.ByregotsBlessing))
            {
                var newQuality = GreatStridesByregotCombo(craft, step);
                var newHQPercent = maxQuality > 0 ? Calculations.GetHQChance(newQuality * 100.0 / maxQuality) : 100;
                var newDone = craft.CraftQualityMin1 == 0 ? newHQPercent >= _config.MaxPercentage : newQuality >= maxQuality;
                if (newDone) return new(VulcanSkill.GreatStrides, "GS Combo");
            }

            if (step.Condition == Condition.Poor && Simulator.CanUseAction(craft, step, VulcanSkill.CarefulObservation) && _config.UseSpecialist) return new(VulcanSkill.CarefulObservation);
            if (step.Condition == Condition.Poor && Simulator.CanUseAction(craft, step, VulcanSkill.Observe))
            {
                if (step.InnovationLeft >= 2 && craft.StatLevel >= Simulator.MinLevel(VulcanSkill.AdvancedTouch))
                    return new(VulcanSkill.Observe);

                if (!CanFinishCraft(craft, step, act))
                    return new(act);

                return new(VulcanSkill.Observe);
            }
            if (step.GreatStridesLeft != 0 && Simulator.CanUseAction(craft, step, VulcanSkill.ByregotsBlessing) && !WillActFail(craft, step, VulcanSkill.ByregotsBlessing)) return new(VulcanSkill.ByregotsBlessing);
            if (step.HeartAndSoulAvailable && Simulator.CanUseAction(craft, step, VulcanSkill.HeartAndSoul) && _config.UseSpecialist) return new(VulcanSkill.HeartAndSoul);
            if (HighestLevelTouch(craft, step) is var touch && touch != VulcanSkill.None) return new(touch);
        }

        if (CanFinishCraft(craft, step, act))
            return new(act);

        if (Simulator.CanUseAction(craft, step, VulcanSkill.Veneration) && step.VenerationLeft == 0 && step.Condition != Condition.Excellent) return new(VulcanSkill.Veneration);
        return new(act);
    }

    private bool CheckIfVenerationIsWorth(CraftState craft, StepState step, VulcanSkill act)
    {
        if (step.Condition is Condition.Good or Condition.Excellent) return false;
        if (_venerationUsed) return false;
        if (step.FinalAppraisalLeft > 0) return false;

        var (result, next) = Simulator.Execute(craft, step with { Durability = 40 }, act, 0, 1);
        if (next.Progress >= craft.CraftProgress) return false;
        var (result2, next2) = Simulator.Execute(craft, next with { Durability = 40 }, act, 0, 1);
        if (next2.Progress >= craft.CraftProgress) return false;

        return true;
    }

    private static bool WillActFail(CraftState craft, StepState step, VulcanSkill act)
    {
        bool result = step.Durability - Simulator.GetDurabilityCost(step, act) <= 0 && CalculateNewProgress(craft, step, act) < craft.CraftProgress;
        return result;
    }

    private bool GoingForQuality(CraftState craft, StepState step, out int maxQuality)
    {
        bool wantMoreQuality;
        if (craft.CraftQualityMin1 == 0)
        {
            maxQuality = craft.CraftQualityMax;
            wantMoreQuality = maxQuality > 0 && Calculations.GetHQChance(step.Quality * 100.0 / maxQuality) < _config.MaxPercentage;
        }
        else
        {
            maxQuality = _config.SolverCollectibleMode switch
            {
                1 => craft.CraftQualityMin1,
                2 => craft.CraftQualityMin2,
                _ => craft.CraftQualityMin3,
            };
            wantMoreQuality = step.Quality < maxQuality;
        }

        return wantMoreQuality;
    }

    private static int GetComboDurability(CraftState craft, StepState step, params VulcanSkill[] comboskills)
    {
        int output = step.Durability;
        foreach (var skill in comboskills)
        {
            var (result, next) = Simulator.Execute(craft, step, skill, 1, 0);
            if (result == Simulator.ExecuteResult.CantUse)
                continue;

            output = next.Durability;
            step = next;
        }

        return output;
    }

    public static int CalculateNewProgress(CraftState craft, StepState step, VulcanSkill action) => step.FinalAppraisalLeft > 0 ? Math.Min(step.Progress + Simulator.CalculateProgress(craft, step, action), craft.CraftProgress - 1) : step.Progress + Simulator.CalculateProgress(craft, step, action);
    public static int CalculateNewQuality(CraftState craft, StepState step, VulcanSkill action) => step.Quality + Simulator.CalculateQuality(craft, step, action);
    public static bool CanFinishCraft(CraftState craft, StepState step, VulcanSkill act) => CalculateNewProgress(craft, step, act) >= craft.CraftProgress;

    public static int GreatStridesByregotCombo(CraftState craft, StepState step)
    {
        if (!Simulator.CanUseAction(craft, step, VulcanSkill.ByregotsBlessing) || step.RemainingCP < 56)
            return 0;

        var (res, next) = Simulator.Execute(craft, step, VulcanSkill.GreatStrides, 0, 1);
        if (res != Simulator.ExecuteResult.Succeeded)
            return 0;

        return CalculateNewQuality(craft, next, VulcanSkill.ByregotsBlessing);
    }

    public VulcanSkill HighestLevelTouch(CraftState craft, StepState step)
    {
        bool wasteNots = step.WasteNotLeft > 0;

        if (Simulator.CanUseAction(craft, step, VulcanSkill.AdvancedTouch) && step.PrevComboAction == VulcanSkill.Observe) return VulcanSkill.AdvancedTouch;
        if (Simulator.CanUseAction(craft, step, VulcanSkill.PreciseTouch)) return VulcanSkill.PreciseTouch;
        if (Simulator.CanUseAction(craft, step, VulcanSkill.PreparatoryTouch) && step.IQStacks < _config.MaxIQPrepTouch && step.InnovationLeft > 0) return VulcanSkill.PreparatoryTouch;
        if (Simulator.CanUseAction(craft, step, VulcanSkill.AdvancedTouch) && step.PrevComboAction == VulcanSkill.StandardTouch) return VulcanSkill.AdvancedTouch;
        if (Simulator.CanUseAction(craft, step, VulcanSkill.StandardTouch) && step.PrevComboAction == VulcanSkill.BasicTouch) return VulcanSkill.StandardTouch;
        if (Simulator.CanUseAction(craft, step, VulcanSkill.PrudentTouch) && GetComboDurability(craft, step, VulcanSkill.BasicTouch, VulcanSkill.StandardTouch, VulcanSkill.AdvancedTouch) <= 0) return VulcanSkill.PrudentTouch;
        if (Simulator.CanUseAction(craft, step, VulcanSkill.TrainedFinesse) && step.Durability <= 10) return VulcanSkill.TrainedFinesse;
        if (Simulator.CanUseAction(craft, step, VulcanSkill.BasicTouch)) return VulcanSkill.BasicTouch;
        if (Simulator.CanUseAction(craft, step, VulcanSkill.DaringTouch)) return VulcanSkill.DaringTouch;
        if (Simulator.CanUseAction(craft, step, VulcanSkill.HastyTouch)) return VulcanSkill.HastyTouch;

        return VulcanSkill.None;
    }

    public static VulcanSkill HighestLevelSynth(CraftState craft, StepState step)
    {
        if (Simulator.CanUseAction(craft, step, VulcanSkill.IntensiveSynthesis)) return VulcanSkill.IntensiveSynthesis;
        if (Simulator.CanUseAction(craft, step, VulcanSkill.Groundwork) && step.Durability > 20) return VulcanSkill.Groundwork;
        if (Simulator.CanUseAction(craft, step, VulcanSkill.PrudentSynthesis)) return VulcanSkill.PrudentSynthesis;
        if (Simulator.CanUseAction(craft, step, VulcanSkill.CarefulSynthesis)) return VulcanSkill.CarefulSynthesis;
        if (Simulator.CanUseAction(craft, step, VulcanSkill.BasicSynthesis)) return VulcanSkill.BasicSynthesis;

        return VulcanSkill.None;
    }
}
