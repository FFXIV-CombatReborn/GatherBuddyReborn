using Dalamud.Plugin.Services;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace GatherBuddy.Crafting;

public static class CraftingStateBuilder
{
public static GameStateBuilder.PlayerStats GetCurrentPlayerStats()
    {
        try
        {
            var player = Dalamud.ClientState.LocalPlayer;
            if (player == null)
            {
                return new GameStateBuilder.PlayerStats(
                    Craftsmanship: 100,
                    Control: 100,
                    CP: 180,
                    Level: 1,
                    Manipulation: false,
                    Specialist: false,
                    SplendorCosmic: false
                );
            }

            var level = player.Level;
            var jobId = player.ClassJob.RowId;
            var isCrafter = jobId is >= 8 and <= 15;

            if (!isCrafter)
            {
                return new GameStateBuilder.PlayerStats(
                    Craftsmanship: 100,
                    Control: 100,
                    CP: 180,
                    Level: level,
                    Manipulation: false,
                    Specialist: false,
                    SplendorCosmic: false
                );
            }

            var stats = new GameStateBuilder.PlayerStats(
                Craftsmanship: GetCraftsmanshipStat() ?? 100,
                Control: GetControlStat() ?? 100,
                CP: GetMaxCPStat() ?? 180,
                Level: level,
                Manipulation: GetManipulationUnlocked(jobId),
                Specialist: GetIsSpecialist(jobId),
                SplendorCosmic: GetSplendorCosmic()
            );

            return stats;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingStateBuilder] Failed to get current player stats: {ex.Message}");
            return new GameStateBuilder.PlayerStats(
                Craftsmanship: 100,
                Control: 100,
                CP: 180,
                Level: 1,
                Manipulation: false,
                Specialist: false,
                SplendorCosmic: false
            );
        }
    }

    private static int? GetCraftsmanshipStat()
    {
        try
        {
            unsafe
            {
                var playerState = PlayerState.Instance();
                if (playerState == null)
                    return null;
                return playerState->Attributes[70];
            }
        }
        catch
        {
            return null;
        }
    }

    private static int? GetControlStat()
    {
        try
        {
            unsafe
            {
                var playerState = PlayerState.Instance();
                if (playerState == null)
                    return null;
                return playerState->Attributes[71];
            }
        }
        catch
        {
            return null;
        }
    }

    private static int? GetMaxCPStat()
    {
        try
        {
            unsafe
            {
                var playerState = PlayerState.Instance();
                if (playerState == null)
                    return null;
                return playerState->Attributes[11];
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool GetManipulationUnlocked(uint jobId)
    {
        try
        {
            unsafe
            {
                var manipulationQuestId = jobId switch
                {
                    8 => 67979u,  // CRP
                    9 => 68153u,  // BSM
                    10 => 68132u, // ARM
                    11 => 68137u, // GSM
                    12 => 68147u, // LTW
                    13 => 67969u, // WVR
                    14 => 67974u, // ALC
                    15 => 68142u, // CUL
                    _ => 0u
                };

                if (manipulationQuestId == 0)
                    return false;

                return QuestManager.IsQuestComplete(manipulationQuestId);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool GetIsSpecialist(uint jobId)
    {
        try
        {
            var player = Dalamud.ClientState.LocalPlayer;
            if (player == null)
                return false;

            unsafe
            {
                var inventoryManager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                if (inventoryManager == null)
                    return false;

                var jobCrystal = inventoryManager->GetInventorySlot(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.EquippedItems, 13);
                return jobCrystal != null && jobCrystal->ItemId != 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool GetSplendorCosmic()
    {
        try
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static GameStateBuilder.RecipeInfo BuildRecipeInfo(Recipe recipe)
    {
        var lt = recipe.RecipeLevelTable.Value;
        var difficulty = (int)(lt.Difficulty * recipe.DifficultyFactor / 100);
        var qualityMax = (int)(lt.Quality * recipe.QualityFactor / 100);
        var durability = (int)(lt.Durability * recipe.DurabilityFactor / 100);
        var qualityMin1 = 0;
        var qualityMin2 = 0;
        var qualityMin3 = 0;
        var isCollectible = recipe.ItemResult.Value.AlwaysCollectable;

        if (isCollectible)
        {
            var itemId = recipe.ItemResult.RowId;
            var found = false;

            var hwdSheet = Dalamud.GameData.GetExcelSheet<HWDCrafterSupply>();
            if (hwdSheet != null)
            {
                foreach (var row in hwdSheet)
                {
                    foreach (var param in row.HWDCrafterSupplyParams)
                    {
                        if (param.ItemTradeIn.RowId != itemId)
                            continue;

                        qualityMin1 = param.BaseCollectableRating * 10;
                        qualityMin2 = param.MidCollectableRating * 10;
                        qualityMin3 = param.HighCollectableRating * 10;
                        found = true;
                        break;
                    }

                    if (found)
                        break;
                }
            }

            if (!found)
            {
                var satisfactionSheet = Dalamud.GameData.GetSubrowExcelSheet<SatisfactionSupply>();
                if (satisfactionSheet != null)
                {
                    foreach (var row in satisfactionSheet.SelectMany(sheet => sheet))
                    {
                        if (row.Item.RowId != itemId)
                            continue;

                        qualityMin1 = row.CollectabilityLow * 10;
                        qualityMin2 = row.CollectabilityMid * 10;
                        qualityMin3 = row.CollectabilityHigh * 10;
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                var sharlayanSheet = Dalamud.GameData.GetExcelSheet<SharlayanCraftWorksSupply>();
                if (sharlayanSheet != null)
                {
                    foreach (var row in sharlayanSheet)
                    {
                        foreach (var entry in row.Item)
                        {
                            if (entry.ItemId.RowId != itemId)
                                continue;

                            qualityMin1 = entry.CollectabilityMid * 10;
                            qualityMin2 = entry.CollectabilityHigh * 10;
                            found = true;
                            break;
                        }

                        if (found)
                            break;
                    }
                }
            }

            if (!found)
            {
                var bankaSheet = Dalamud.GameData.GetExcelSheet<BankaCraftWorksSupply>();
                if (bankaSheet != null)
                {
                    foreach (var row in bankaSheet)
                    {
                        foreach (var entry in row.Item)
                        {
                            if (entry.ItemId.RowId != itemId)
                                continue;

                            var breakpoints = entry.Collectability.Value;
                            qualityMin1 = breakpoints.CollectabilityLow * 10;
                            qualityMin2 = breakpoints.CollectabilityMid * 10;
                            qualityMin3 = breakpoints.CollectabilityHigh * 10;
                            found = true;
                            break;
                        }

                        if (found)
                            break;
                    }
                }
            }

            if (!found)
            {
                var collectableSheet = Dalamud.GameData.GetSubrowExcelSheet<CollectablesShopItem>();
                if (collectableSheet != null)
                {
                    foreach (var row in collectableSheet.SelectMany(sheet => sheet))
                    {
                        if (row.Item.RowId != itemId || row.CollectablesShopRefine.RowId == 0)
                            continue;

                        var breakpoints = row.CollectablesShopRefine.Value;
                        qualityMin1 = breakpoints.LowCollectability * 10;
                        qualityMin2 = breakpoints.MidCollectability * 10;
                        qualityMin3 = breakpoints.HighCollectability * 10;
                        found = true;
                        break;
                    }
                }
            }

            if (qualityMin3 == 0)
            {
                qualityMin3 = qualityMin2;
                qualityMin2 = qualityMin1;
            }
        }
        else if (recipe.RequiredQuality > 0)
        {
            qualityMax = (int)recipe.RequiredQuality;
            qualityMin1 = qualityMax;
            qualityMin2 = qualityMax;
            qualityMin3 = qualityMax;
        }
        else if (recipe.CanHq)
        {
            qualityMin3 = qualityMax;
        }

        return new GameStateBuilder.RecipeInfo(
            RecipeId: recipe.RowId,
            Level: lt.ClassJobLevel,
            Difficulty: difficulty,
            QualityMax: qualityMax,
            Durability: durability,
            ProgressDivider: lt.ProgressDivider,
            ProgressModifier: lt.ProgressModifier,
            QualityDivider: lt.QualityDivider,
            QualityModifier: lt.QualityModifier,
            CanHQ: recipe.CanHq,
            IsExpert: recipe.IsExpert,
            IsCollectible: isCollectible,
            QualityMin1: qualityMin1,
            QualityMin2: qualityMin2,
            QualityMin3: qualityMin3,
            ConditionFlags: (ConditionFlags)lt.ConditionsFlag,
            HasMaterialMiracle: false
        );
    }

    public static CraftState BuildCraftState(Recipe recipe)
    {
        var playerStats = GetCurrentPlayerStats();
        var recipeInfo = BuildRecipeInfo(recipe);
        return GameStateBuilder.BuildCraftState(recipeInfo, playerStats);
    }

    public static StepState BuildInitialStepState(CraftState craft)
    {
        return GameStateBuilder.BuildInitialStepState(craft, startingQuality: 0);
    }
}
