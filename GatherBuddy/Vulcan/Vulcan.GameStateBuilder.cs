using System.Collections.Generic;

namespace GatherBuddy.Vulcan;

public static class GameStateBuilder
{
    public record PlayerStats(int Craftsmanship, int Control, int CP, int Level, bool Manipulation, bool Specialist, bool SplendorCosmic);

    public record RecipeInfo(uint RecipeId, int Level, int Difficulty, int QualityMax, int Durability, int ProgressDivider, int ProgressModifier, int QualityDivider, int QualityModifier, bool CanHQ, bool IsExpert, bool IsCollectible, int QualityMin1, int QualityMin2, int QualityMin3, ConditionFlags ConditionFlags, bool HasMaterialMiracle);

    public static CraftState BuildCraftState(RecipeInfo recipe, PlayerStats playerStats)
    {
        return new CraftState
        {
            StatCraftsmanship = playerStats.Craftsmanship,
            StatControl = playerStats.Control,
            StatCP = playerStats.CP,
            StatLevel = playerStats.Level,
            UnlockedManipulation = playerStats.Manipulation,
            Specialist = playerStats.Specialist,
            SplendorCosmic = playerStats.SplendorCosmic,

            ItemId = recipe.RecipeId,
            RecipeId = recipe.RecipeId,

            CraftExpert = recipe.IsExpert,
            CraftHQ = recipe.CanHQ,
            CraftCollectible = recipe.IsCollectible,
            CraftLevel = recipe.Level,

            CraftDurability = recipe.Durability,
            CraftProgress = recipe.Difficulty,
            CraftQualityMax = recipe.QualityMax,

            CraftQualityMin1 = recipe.QualityMin1,
            CraftQualityMin2 = recipe.QualityMin2,
            CraftQualityMin3 = recipe.QualityMin3,

            CraftProgressDivider = recipe.ProgressDivider,
            CraftProgressModifier = recipe.ProgressModifier,
            CraftQualityDivider = recipe.QualityDivider,
            CraftQualityModifier = recipe.QualityModifier,

            CraftRequiredQuality = 0,
            CraftRecommendedCraftsmanship = 0,

            ConditionFlags = recipe.ConditionFlags,
            MissionHasMaterialMiracle = recipe.HasMaterialMiracle,
            InitialQuality = 0,

            CraftConditionProbabilities = GetConditionProbabilities(recipe.ConditionFlags),
            IsCosmic = false
        };
    }

    public static StepState BuildInitialStepState(CraftState craft, int startingQuality = 0)
    {
        return new StepState
        {
            Index = 1,
            Progress = 0,
            Quality = startingQuality,
            Durability = craft.CraftDurability,
            RemainingCP = craft.StatCP,

            Condition = Condition.Normal,
            IQStacks = 0,

            WasteNotLeft = 0,
            ManipulationLeft = 0,
            GreatStridesLeft = 0,
            InnovationLeft = 0,
            VenerationLeft = 0,
            MuscleMemoryLeft = 0,
            FinalAppraisalLeft = 0,
            CarefulObservationLeft = craft.Specialist ? 2 : 0,
            ExpedienceLeft = 0,
            QuickInnoLeft = 0,

            HeartAndSoulActive = false,
            HeartAndSoulAvailable = craft.Specialist,
            QuickInnoAvailable = false,
            TrainedPerfectionAvailable = true,
            TrainedPerfectionActive = false,
            MaterialMiracleActive = false,

            PrevActionFailed = false,
            PrevComboAction = VulcanSkill.None,
            MaterialMiracleCharges = craft.MissionHasMaterialMiracle ? 1u : 0u,
            ObserveCounter = 0
        };
    }

    public static StepState UpdateStepState(StepState current, GameStateSnapshot gameState)
    {
        var updated = current with
        {
            Durability = gameState.CurrentDurability,
            RemainingCP = gameState.CurrentCP,
            Quality = gameState.CurrentQuality,
            Progress = gameState.CurrentProgress,
            Condition = gameState.CurrentCondition,
            IQStacks = gameState.InnerQuietStacks,

            WasteNotLeft = gameState.BuffDurations.GetValueOrDefault("WasteNot", 0),
            ManipulationLeft = gameState.BuffDurations.GetValueOrDefault("Manipulation", 0),
            GreatStridesLeft = gameState.BuffDurations.GetValueOrDefault("GreatStrides", 0),
            InnovationLeft = gameState.BuffDurations.GetValueOrDefault("Innovation", 0),
            VenerationLeft = gameState.BuffDurations.GetValueOrDefault("Veneration", 0),
            MuscleMemoryLeft = gameState.BuffDurations.GetValueOrDefault("MuscleMemory", 0),
            FinalAppraisalLeft = gameState.BuffDurations.GetValueOrDefault("FinalAppraisal", 0),
            CarefulObservationLeft = gameState.BuffDurations.GetValueOrDefault("CarefulObservation", 0),
            QuickInnoLeft = gameState.BuffDurations.GetValueOrDefault("QuickInnovation", 0),

            HeartAndSoulActive = gameState.HeartAndSoulActive,
            HeartAndSoulAvailable = gameState.HeartAndSoulAvailable,
            QuickInnoAvailable = gameState.QuickInnoAvailable,
            TrainedPerfectionAvailable = gameState.TrainedPerfectionAvailable,
            TrainedPerfectionActive = gameState.TrainedPerfectionActive,
            MaterialMiracleCharges = gameState.MaterialMiracleCharges,
            MaterialMiracleActive = gameState.MaterialMiracleActive
        };

        return updated;
    }

    public record GameStateSnapshot(
        int CurrentDurability,
        int CurrentCP,
        int CurrentQuality,
        int CurrentProgress,
        Condition CurrentCondition,
        int InnerQuietStacks,
        Dictionary<string, int> BuffDurations,
        bool HeartAndSoulActive,
        bool HeartAndSoulAvailable,
        bool QuickInnoAvailable,
        bool TrainedPerfectionAvailable,
        bool TrainedPerfectionActive,
        uint MaterialMiracleCharges,
        bool MaterialMiracleActive
    );

    private static float[] GetConditionProbabilities(ConditionFlags flags)
    {
        if ((flags & ConditionFlags.Normal) == 0)
            return new[] { 1f };

        var probs = new float[10];
        probs[0] = 1f;

        if ((flags & ConditionFlags.Good) != 0) probs[1] = 0.2f;
        if ((flags & ConditionFlags.Excellent) != 0) probs[2] = 0.04f;
        if ((flags & ConditionFlags.Poor) != 0) probs[3] = 0.05f;
        if ((flags & ConditionFlags.Centered) != 0) probs[4] = 0.1f;
        if ((flags & ConditionFlags.Sturdy) != 0) probs[5] = 0.1f;
        if ((flags & ConditionFlags.Pliant) != 0) probs[6] = 0.1f;
        if ((flags & ConditionFlags.Malleable) != 0) probs[7] = 0.1f;
        if ((flags & ConditionFlags.Primed) != 0) probs[8] = 0.05f;
        if ((flags & ConditionFlags.GoodOmen) != 0) probs[9] = 0.05f;

        return probs;
    }
}
