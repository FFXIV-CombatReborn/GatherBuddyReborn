using System;

namespace GatherBuddy.Vulcan;

public static class Simulator
    {
        public enum ExecuteResult
        {
            CantUse,
            Failed,
            Succeeded
        }

        public static (ExecuteResult, StepState) Execute(CraftState craft, StepState step, VulcanSkill action, float actionSuccessRoll, float nextStateRoll)
        {
            if (!CanUseAction(craft, step, action))
                return (ExecuteResult.CantUse, step);

            var success = actionSuccessRoll < GetSuccessRate(step, action);

            var next = new StepState();
            next.Index = SkipUpdates(action) ? step.Index : step.Index + 1;
            next.Progress = step.Progress + (success ? CalculateProgress(craft, step, action) : 0);
            next.Quality = step.Quality + (success ? CalculateQuality(craft, step, action) : 0);
            next.IQStacks = step.IQStacks;
            if (success)
            {
                if (next.Quality != step.Quality)
                    ++next.IQStacks;
                if (action is VulcanSkill.PreciseTouch or VulcanSkill.PreparatoryTouch or VulcanSkill.Reflect or VulcanSkill.RefinedTouch)
                    ++next.IQStacks;
                if (next.IQStacks > 10)
                    next.IQStacks = 10;
                if (action == VulcanSkill.ByregotsBlessing)
                    next.IQStacks = 0;
                if (action == VulcanSkill.HastyTouch)
                    next.ExpedienceLeft = 1;
                else
                    next.ExpedienceLeft = 0;
            }

            next.WasteNotLeft = action switch
            {
                VulcanSkill.WasteNot => GetNewBuffDuration(step, 4),
                VulcanSkill.WasteNot2 => GetNewBuffDuration(step, 8),
                _ => GetOldBuffDuration(step.WasteNotLeft, action)
            };
            next.ManipulationLeft = action == VulcanSkill.Manipulation ? GetNewBuffDuration(step, 8) : GetOldBuffDuration(step.ManipulationLeft, action);
            next.GreatStridesLeft = action == VulcanSkill.GreatStrides ? GetNewBuffDuration(step, 3) : GetOldBuffDuration(step.GreatStridesLeft, action, next.Quality != step.Quality);
            next.InnovationLeft = action == VulcanSkill.Innovation ? GetNewBuffDuration(step, 4) : action == VulcanSkill.QuickInnovation ? GetNewBuffDuration(step, 1) : GetOldBuffDuration(step.InnovationLeft, action);
            next.VenerationLeft = action == VulcanSkill.Veneration ? GetNewBuffDuration(step, 4) : GetOldBuffDuration(step.VenerationLeft, action);
            next.MuscleMemoryLeft = action == VulcanSkill.MuscleMemory ? GetNewBuffDuration(step, 5) : GetOldBuffDuration(step.MuscleMemoryLeft, action, next.Progress != step.Progress);
            next.FinalAppraisalLeft = action == VulcanSkill.FinalAppraisal ? GetNewBuffDuration(step, 5) : GetOldBuffDuration(step.FinalAppraisalLeft, action, next.Progress >= craft.CraftProgress);
            next.CarefulObservationLeft = step.CarefulObservationLeft - (action == VulcanSkill.CarefulObservation ? 1 : 0);
            next.HeartAndSoulActive = action == VulcanSkill.HeartAndSoul || step.HeartAndSoulActive && (step.Condition is Condition.Good or Condition.Excellent || !ConsumeHeartAndSoul(action));
            next.HeartAndSoulAvailable = step.HeartAndSoulAvailable && action != VulcanSkill.HeartAndSoul;
            next.QuickInnoLeft = step.QuickInnoLeft - (action == VulcanSkill.QuickInnovation ? 1 : 0);
            next.QuickInnoAvailable = step.QuickInnoLeft > 0 && next.InnovationLeft == 0;
            next.PrevActionFailed = !success;
            next.PrevComboAction = action;
            next.TrainedPerfectionActive = action == VulcanSkill.TrainedPerfection || (step.TrainedPerfectionActive && !HasDurabilityCost(action));
            next.TrainedPerfectionAvailable = step.TrainedPerfectionAvailable && action != VulcanSkill.TrainedPerfection;
            next.MaterialMiracleCharges = action == VulcanSkill.MaterialMiracle ? step.MaterialMiracleCharges - 1 : step.MaterialMiracleCharges;
            next.MaterialMiracleActive = step.MaterialMiracleActive;
            next.ObserveCounter = action == VulcanSkill.Observe ? step.ObserveCounter + 1 : 0;

            if (step.FinalAppraisalLeft > 0 && next.Progress >= craft.CraftProgress)
                next.Progress = craft.CraftProgress - 1;

            next.RemainingCP = step.RemainingCP - GetCPCost(step, action);
            if (action == VulcanSkill.TricksOfTrade)
                next.RemainingCP = Math.Min(craft.StatCP, next.RemainingCP + 20);

            next.Durability = step.Durability - GetDurabilityCost(step, action);
            if (next.Durability > 0)
            {
                int repair = 0;
                if (action == VulcanSkill.MastersMend)
                    repair += 30;
                if (action == VulcanSkill.ImmaculateMend)
                    repair = craft.CraftDurability;
                if (step.ManipulationLeft > 0 && action != VulcanSkill.Manipulation && !SkipUpdates(action) && next.Progress < craft.CraftProgress)
                    repair += 5;
                next.Durability = Math.Min(craft.CraftDurability, next.Durability + repair);
            }

            next.Condition = action is VulcanSkill.FinalAppraisal or VulcanSkill.HeartAndSoul ? step.Condition : GetNextCondition(craft, step, nextStateRoll);

            return (success ? ExecuteResult.Succeeded : ExecuteResult.Failed, next);
        }

        private static bool HasDurabilityCost(VulcanSkill action)
        {
            var cost = action switch
            {
                VulcanSkill.BasicSynthesis or VulcanSkill.CarefulSynthesis or VulcanSkill.RapidSynthesis or VulcanSkill.IntensiveSynthesis or VulcanSkill.MuscleMemory => 10,
                VulcanSkill.BasicTouch or VulcanSkill.StandardTouch or VulcanSkill.AdvancedTouch or VulcanSkill.HastyTouch or VulcanSkill.PreciseTouch or VulcanSkill.Reflect or VulcanSkill.RefinedTouch => 10,
                VulcanSkill.ByregotsBlessing or VulcanSkill.DelicateSynthesis => 10,
                VulcanSkill.Groundwork or VulcanSkill.PreparatoryTouch => 20,
                VulcanSkill.PrudentSynthesis or VulcanSkill.PrudentTouch => 5,
                _ => 0
            };
            return cost > 0;
        }

        public static int BaseProgress(CraftState craft)
        {
            float res = craft.StatCraftsmanship * 10.0f / craft.CraftProgressDivider + 2;
            if (craft.StatLevel <= craft.CraftLevel)
                res = res * craft.CraftProgressModifier / 100;
            return (int)res;
        }

        public static int BaseQuality(CraftState craft)
        {
            float res = craft.StatControl * 10.0f / craft.CraftQualityDivider + 35;
            if (craft.StatLevel <= craft.CraftLevel)
                res = res * craft.CraftQualityModifier / 100;
            return (int)res;
        }

        public static bool CanUseAction(CraftState craft, StepState step, VulcanSkill action) => action switch
        {
            VulcanSkill.IntensiveSynthesis or VulcanSkill.PreciseTouch or VulcanSkill.TricksOfTrade => step.Condition is Condition.Good or Condition.Excellent || step.HeartAndSoulActive,
            VulcanSkill.PrudentSynthesis or VulcanSkill.PrudentTouch => step.WasteNotLeft == 0,
            VulcanSkill.MuscleMemory or VulcanSkill.Reflect => step.Index == 1,
            VulcanSkill.TrainedFinesse => step.IQStacks == 10,
            VulcanSkill.ByregotsBlessing => step.IQStacks > 0,
            VulcanSkill.TrainedEye => !craft.CraftExpert && craft.StatLevel >= craft.CraftLevel + 10 && step.Index == 1,
            VulcanSkill.Manipulation => craft.UnlockedManipulation,
            VulcanSkill.CarefulObservation => step.CarefulObservationLeft > 0,
            VulcanSkill.HeartAndSoul => step.HeartAndSoulAvailable,
            VulcanSkill.TrainedPerfection => step.TrainedPerfectionAvailable,
            VulcanSkill.DaringTouch => step.ExpedienceLeft > 0,
            VulcanSkill.QuickInnovation => step.QuickInnoLeft > 0 && step.InnovationLeft == 0,
            VulcanSkill.MaterialMiracle => step.MaterialMiracleCharges > 0 && !step.MaterialMiracleActive,
            _ => true
        } && craft.StatLevel >= MinLevel(action) && step.RemainingCP >= GetCPCost(step, action);

        public static bool SkipUpdates(VulcanSkill action) => action is VulcanSkill.CarefulObservation or VulcanSkill.FinalAppraisal or VulcanSkill.HeartAndSoul or VulcanSkill.MaterialMiracle;
        public static bool ConsumeHeartAndSoul(VulcanSkill action) => action is VulcanSkill.IntensiveSynthesis or VulcanSkill.PreciseTouch or VulcanSkill.TricksOfTrade;

        public static int MinLevel(VulcanSkill action) => action switch
        {
            VulcanSkill.BasicSynthesis => 1,
            VulcanSkill.BasicTouch => 5,
            VulcanSkill.MastersMend => 7,
            VulcanSkill.HastyTouch => 9,
            VulcanSkill.RapidSynthesis => 9,
            VulcanSkill.Observe => 13,
            VulcanSkill.TricksOfTrade => 13,
            VulcanSkill.WasteNot => 15,
            VulcanSkill.Veneration => 15,
            VulcanSkill.StandardTouch => 18,
            VulcanSkill.GreatStrides => 21,
            VulcanSkill.Innovation => 26,
            VulcanSkill.FinalAppraisal => 42,
            VulcanSkill.WasteNot2 => 47,
            VulcanSkill.ByregotsBlessing => 50,
            VulcanSkill.PreciseTouch => 53,
            VulcanSkill.MuscleMemory => 54,
            VulcanSkill.CarefulObservation => 55,
            VulcanSkill.CarefulSynthesis => 62,
            VulcanSkill.Manipulation => 65,
            VulcanSkill.PrudentTouch => 66,
            VulcanSkill.AdvancedTouch => 68,
            VulcanSkill.Reflect => 69,
            VulcanSkill.PreparatoryTouch => 71,
            VulcanSkill.Groundwork => 72,
            VulcanSkill.DelicateSynthesis => 76,
            VulcanSkill.IntensiveSynthesis => 78,
            VulcanSkill.TrainedEye => 80,
            VulcanSkill.HeartAndSoul => 86,
            VulcanSkill.PrudentSynthesis => 88,
            VulcanSkill.TrainedFinesse => 90,
            VulcanSkill.RefinedTouch => 92,
            VulcanSkill.QuickInnovation => 96,
            VulcanSkill.DaringTouch => 96,
            VulcanSkill.ImmaculateMend => 98,
            VulcanSkill.TrainedPerfection => 100,
            VulcanSkill.MaterialMiracle => 101,
            _ => 1
        };

        public static double GetSuccessRate(StepState step, VulcanSkill action)
        {
            var rate = action switch
            {
                VulcanSkill.RapidSynthesis => 0.5,
                VulcanSkill.HastyTouch or VulcanSkill.DaringTouch => 0.6,
                _ => 1.0
            };
            if (step.Condition == Condition.Centered)
                rate += 0.25;
            return rate;
        }

        public static int GetBaseCPCost(VulcanSkill action, VulcanSkill prevAction) => action switch
        {
            VulcanSkill.CarefulSynthesis => 7,
            VulcanSkill.Groundwork => 18,
            VulcanSkill.IntensiveSynthesis => 6,
            VulcanSkill.PrudentSynthesis => 18,
            VulcanSkill.MuscleMemory => 6,
            VulcanSkill.BasicTouch => 18,
            VulcanSkill.StandardTouch => prevAction == VulcanSkill.BasicTouch ? 18 : 32,
            VulcanSkill.AdvancedTouch => prevAction is VulcanSkill.StandardTouch or VulcanSkill.Observe ? 18 : 46,
            VulcanSkill.PreparatoryTouch => 40,
            VulcanSkill.PreciseTouch => 18,
            VulcanSkill.PrudentTouch => 25,
            VulcanSkill.TrainedFinesse => 32,
            VulcanSkill.Reflect => 6,
            VulcanSkill.ByregotsBlessing => 24,
            VulcanSkill.TrainedEye => 250,
            VulcanSkill.DelicateSynthesis => 32,
            VulcanSkill.Veneration => 18,
            VulcanSkill.Innovation => 18,
            VulcanSkill.GreatStrides => 32,
            VulcanSkill.MastersMend => 88,
            VulcanSkill.Manipulation => 96,
            VulcanSkill.WasteNot => 56,
            VulcanSkill.WasteNot2 => 98,
            VulcanSkill.Observe => 7,
            VulcanSkill.FinalAppraisal => 1,
            VulcanSkill.RefinedTouch => 24,
            VulcanSkill.ImmaculateMend => 112,
            _ => 0
        };

        public static int GetCPCost(StepState step, VulcanSkill action)
        {
            var cost = GetBaseCPCost(action, step.PrevComboAction);
            if (step.Condition == Condition.Pliant)
                cost -= cost / 2;
            return cost;
        }

        public static int GetDurabilityCost(StepState step, VulcanSkill action)
        {
            if (step.TrainedPerfectionActive) return 0;
            var cost = action switch
            {
                VulcanSkill.BasicSynthesis or VulcanSkill.CarefulSynthesis or VulcanSkill.RapidSynthesis or VulcanSkill.IntensiveSynthesis or VulcanSkill.MuscleMemory => 10,
                VulcanSkill.BasicTouch or VulcanSkill.StandardTouch or VulcanSkill.AdvancedTouch or VulcanSkill.HastyTouch or VulcanSkill.DaringTouch or VulcanSkill.PreciseTouch or VulcanSkill.Reflect or VulcanSkill.RefinedTouch => 10,
                VulcanSkill.ByregotsBlessing or VulcanSkill.DelicateSynthesis => 10,
                VulcanSkill.Groundwork or VulcanSkill.PreparatoryTouch => 20,
                VulcanSkill.PrudentSynthesis or VulcanSkill.PrudentTouch => 5,
                _ => 0
            };
            if (step.WasteNotLeft > 0)
                cost -= cost / 2;
            if (step.Condition == Condition.Sturdy)
                cost -= cost / 2;
            return cost;
        }

        public static int GetNewBuffDuration(StepState step, int baseDuration) => baseDuration + (step.Condition == Condition.Primed ? 2 : 0);
        public static int GetOldBuffDuration(int prevDuration, VulcanSkill action, bool consume = false) => consume || prevDuration == 0 ? 0 : SkipUpdates(action) ? prevDuration : prevDuration - 1;

        public static int CalculateProgress(CraftState craft, StepState step, VulcanSkill action)
        {
            int potency = action switch
            {
                VulcanSkill.BasicSynthesis => craft.StatLevel >= 31 ? 120 : 100,
                VulcanSkill.CarefulSynthesis => craft.StatLevel >= 82 ? 180 : 150,
                VulcanSkill.RapidSynthesis => craft.StatLevel >= 63 ? 500 : 250,
                VulcanSkill.Groundwork => step.Durability >= GetDurabilityCost(step, action) ? craft.StatLevel >= 86 ? 360 : 300 : craft.StatLevel >= 86 ? 180 : 150,
                VulcanSkill.IntensiveSynthesis => 400,
                VulcanSkill.PrudentSynthesis => 180,
                VulcanSkill.MuscleMemory => 300,
                VulcanSkill.DelicateSynthesis => craft.StatLevel >= 94 ? 150 : 100,
                _ => 0
            };
            if (potency == 0)
                return 0;

            float buffMod = 1 + (step.MuscleMemoryLeft > 0 ? 1 : 0) + (step.VenerationLeft > 0 ? 0.5f : 0);
            float effPotency = potency * buffMod;

            float condMod = step.Condition == Condition.Malleable ? 1.5f : 1;
            return (int)(BaseProgress(craft) * condMod * effPotency / 100);
        }

        public static int CalculateQuality(CraftState craft, StepState step, VulcanSkill action)
        {
            if (action == VulcanSkill.TrainedEye)
                return craft.CraftQualityMax;

            int potency = action switch
            {
                VulcanSkill.BasicTouch => 100,
                VulcanSkill.StandardTouch => 125,
                VulcanSkill.AdvancedTouch => 150,
                VulcanSkill.HastyTouch => 100,
                VulcanSkill.DaringTouch => 150,
                VulcanSkill.PreparatoryTouch => 200,
                VulcanSkill.PreciseTouch => 150,
                VulcanSkill.PrudentTouch => 100,
                VulcanSkill.TrainedFinesse => 100,
                VulcanSkill.Reflect => 300,
                VulcanSkill.ByregotsBlessing => 100 + 20 * step.IQStacks,
                VulcanSkill.DelicateSynthesis => 100,
                VulcanSkill.RefinedTouch => 100,
                _ => 0
            };
            if (potency == 0)
                return 0;

            float buffMod = (1 + (step.GreatStridesLeft > 0 ? 1 : 0) + (step.InnovationLeft > 0 ? 0.5f : 0)) * (100 + 10 * step.IQStacks) / 100;
            float effPotency = potency * buffMod;

            float condMod = step.Condition switch
            {
                Condition.Good => craft.SplendorCosmic ? 1.75f : 1.5f,
                Condition.Excellent => 4,
                Condition.Poor => 0.5f,
                _ => 1
            };
            return (int)(BaseQuality(craft) * condMod * effPotency / 100);
        }

        public static bool WillFinishCraft(CraftState craft, StepState step, VulcanSkill action) => step.FinalAppraisalLeft == 0 && step.Progress + CalculateProgress(craft, step, action) >= craft.CraftProgress;

        public static VulcanSkill NextTouchCombo(StepState step, CraftState craft)
        {
            if (step.PrevComboAction == VulcanSkill.BasicTouch) return VulcanSkill.StandardTouch;
            if (step.PrevComboAction == VulcanSkill.StandardTouch) return VulcanSkill.AdvancedTouch;
            return VulcanSkill.BasicTouch;
        }

        public static Condition GetNextCondition(CraftState craft, StepState step, float roll) => step.Condition switch
        {
            Condition.Normal => GetTransitionByRoll(craft, step, roll),
            Condition.Good => craft.CraftExpert ? GetTransitionByRoll(craft, step, roll) : Condition.Normal,
            Condition.Excellent => Condition.Poor,
            Condition.Poor => Condition.Normal,
            Condition.GoodOmen => Condition.Good,
            _ => GetTransitionByRoll(craft, step, roll)
        };

        public static Condition GetTransitionByRoll(CraftState craft, StepState step, float roll)
        {
            for (int i = 1; i < craft.CraftConditionProbabilities.Length; ++i)
            {
                roll -= craft.CraftConditionProbabilities[i];
                if (roll < 0)
                    return (Condition)i;
            }
            return Condition.Normal;
        }

        public static ConditionFlags ConditionToFlag(this Condition condition)
        {
            return condition switch
            {
                Condition.Normal => ConditionFlags.Normal,
                Condition.Good => ConditionFlags.Good,
                Condition.Excellent => ConditionFlags.Excellent,
                Condition.Poor => ConditionFlags.Poor,
                Condition.Centered => ConditionFlags.Centered,
                Condition.Sturdy => ConditionFlags.Sturdy,
                Condition.Pliant => ConditionFlags.Pliant,
                Condition.Malleable => ConditionFlags.Malleable,
                Condition.Primed => ConditionFlags.Primed,
                Condition.GoodOmen => ConditionFlags.GoodOmen,
                Condition.Unknown => throw new NotImplementedException(),
            };
        }
    }
