using GatherBuddy.Crafting;
using GatherBuddy.Deimos.Data;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Deimos.Crafting;

/// builds the cosmic craft context from mission data and kicks a single craft through Vulcan
internal static class CosmicCraft
{
    private const uint MaterialMiracleAction = 41269;
    private const uint SteadyHandAction      = 46843;

    public static CosmicCraftContext ContextFor(CosmicInfo mission)
    {
        var miracle = mission.TemporaryActionId == MaterialMiracleAction;
        var steady  = mission.TemporaryActionId == SteadyHandAction;
        return new CosmicCraftContext(
            HasMaterialMiracle: miracle,
            MaterialMiracleCharges: miracle ? 1u : 0u,
            HasSteadyHand: steady,
            SteadyHandCharges: steady ? 2u : 0u,
            TargetQuality: 0);
    }

    /// starts one synth of the given recipe (dict key = real recipe row id, even for criticals)
    public static bool TryStart(ushort recipeRowId, CosmicInfo mission)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (sheet == null || !sheet.TryGetRow(recipeRowId, out var recipe))
        {
            DeimosLog.Error($"Recipe {recipeRowId} not found, can't craft.");
            return false;
        }

        CraftingGameInterop.StartCosmicCraft(recipe, ContextFor(mission));
        return true;
    }
}
