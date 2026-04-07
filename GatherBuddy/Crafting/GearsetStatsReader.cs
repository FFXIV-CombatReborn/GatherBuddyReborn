using System;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using static FFXIVClientStructs.FFXIV.Client.Game.InventoryType;
using Lumina.Excel.Sheets;
using GatherBuddy.Vulcan;

namespace GatherBuddy.Crafting;

public static unsafe class GearsetStatsReader
{
    private static readonly uint[] StatParamIds = [70, 71, 11]; // Craftsmanship, Control, CP

    public static void RefreshGearsetFromCurrentEquipped(uint jobId)
    {
        GatherBuddy.Log.Information($"[GearsetStatsReader] NOTE: To refresh gearset from saved file, switch to the job and back in-game, or reload the gearset via the UI. The gearset module will sync automatically.");
    }

    private static GameStateBuilder.PlayerStats? ReadFromCurrentlyEquipped(uint jobId)
    {
        try
        {
            var craftsmanship = 0;
            var control = 0;
            var cp = 180;

            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            var materiaSheet = Dalamud.GameData.GetExcelSheet<Materia>();

            if (itemSheet == null || materiaSheet == null)
            {
                GatherBuddy.Log.Debug("[GearsetStatsReader] Item or Materia sheet is null");
                return null;
            }

            var inventoryMgr = InventoryManager.Instance();
            if (inventoryMgr == null)
                return null;

            var equippedContainer = inventoryMgr->GetInventoryContainer(InventoryType.EquippedItems);
            if (equippedContainer == null || equippedContainer->Size == 0)
                return null;
            
            for (int i = 0; i < equippedContainer->Size; i++)
            {
                var inventoryItem = equippedContainer->Items + i;
                if (inventoryItem->ItemId == 0)
                    continue;

                uint actualItemId = inventoryItem->ItemId;
                bool isHQ = inventoryItem->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);

                if (!itemSheet.TryGetRow(actualItemId, out var item))
                    continue;

                var baseParams = item.BaseParam;
                var baseParamValues = item.BaseParamValue;

                int idx = 0;
                foreach (var paramRef in baseParams)
                {
                    var paramId = paramRef.RowId;
                    var paramValue = baseParamValues[idx];

                    if (paramId == 70)
                        craftsmanship += paramValue;
                    else if (paramId == 71)
                        control += paramValue;
                    else if (paramId == 11)
                        cp += paramValue;

                    idx++;
                }

                if (isHQ)
                {
                    var hqParams = item.BaseParamSpecial;
                    if (hqParams.Count > 0)
                    {
                        var hqValues = item.BaseParamValueSpecial;
                        int hqIdx = 0;
                        foreach (var hqParam in hqParams)
                        {
                            var hqParamId = hqParam.RowId;
                            var hqParamValue = hqValues[hqIdx];

                            if (hqParamId == 70)
                                craftsmanship += hqParamValue;
                            else if (hqParamId == 71)
                                control += hqParamValue;
                            else if (hqParamId == 11)
                                cp += hqParamValue;

                            hqIdx++;
                        }
                    }
                }

                for (int m = 0; m < 5; m++)
                {
                    var materiaId = inventoryItem->Materia[m];
                    if (materiaId == 0)
                        continue;

                    if (!materiaSheet.TryGetRow(materiaId, out var materia))
                        continue;

                    var baseParamId = materia.BaseParam.RowId;
                    if (baseParamId == 70)
                        craftsmanship += materia.Value[inventoryItem->MateriaGrades[m]];
                    else if (baseParamId == 71)
                        control += materia.Value[inventoryItem->MateriaGrades[m]];
                    else if (baseParamId == 11)
                        cp += materia.Value[inventoryItem->MateriaGrades[m]];
                }
            }

            var manipulation = IsManipulationUnlocked(jobId);
            var isSpecialist = equippedContainer->Size > 13 && (equippedContainer->Items + 13)->ItemId != 0;

            return new GameStateBuilder.PlayerStats(
                Craftsmanship: craftsmanship,
                Control: control,
                CP: cp,
                Level: 100,
                Manipulation: manipulation,
                Specialist: isSpecialist,
                SplendorCosmic: false
            );
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[GearsetStatsReader] Failed to read currently equipped stats: {ex.Message}");
            return null;
        }
    }

    public static GameStateBuilder.PlayerStats? ReadGearsetStatsForJob(uint jobId)
    {
        try
        {
            var currentJob = Dalamud.ClientState.LocalPlayer?.ClassJob.RowId ?? 0;
            
            if (currentJob == jobId)
            {
                var equippedStats = ReadFromCurrentlyEquipped(jobId);
                
                if (equippedStats != null && equippedStats.Craftsmanship > 0)
                {
                    return equippedStats;
                }
            }

            var gearsetModule = RaptureGearsetModule.Instance();
            if (gearsetModule == null)
                return null;

            fixed (RaptureGearsetModule.GearsetEntry* entries = gearsetModule->Entries)
            {
                for (int i = 0; i < 100; i++)
                {
                    if ((entries[i].Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0)
                        continue;

                    if (entries[i].ClassJob != jobId)
                        continue;

                    return CalculateStatsFromGearset(&entries[i], jobId);
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[GearsetStatsReader] Failed to read gearset stats for job {jobId}: {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    private static GameStateBuilder.PlayerStats? CalculateStatsFromGearset(RaptureGearsetModule.GearsetEntry* gearset, uint jobId)
    {
        try
        {
            var craftsmanship = 0;
            var control = 0;
            var cp = 180;

            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            var materiaSheet = Dalamud.GameData.GetExcelSheet<Materia>();

            if (itemSheet == null || materiaSheet == null)
            {
                GatherBuddy.Log.Debug("[GearsetStatsReader] Item or Materia sheet is null");
                return null;
            }

            for (int i = 0; i < 14; i++)
            {
                var gearItem = gearset->Items[i];
                if (gearItem.ItemId == 0)
                    continue;

                uint actualItemId = gearItem.ItemId % 1000000;
                bool isHQ = gearItem.ItemId >= 1000000;
                if (!itemSheet.TryGetRow(actualItemId, out var item))
                    continue;

                var baseParams = item.BaseParam;
                var baseParamValues = item.BaseParamValue;
                
                int idx = 0;
                foreach (var paramRef in baseParams)
                {
                    var paramId = paramRef.RowId;
                    var paramValue = baseParamValues[idx];

                    if (paramId == 70)
                        craftsmanship += paramValue;
                    else if (paramId == 71)
                        control += paramValue;
                    else if (paramId == 11)
                        cp += paramValue;

                    idx++;
                }

                if (isHQ)
                {
                    var hqParams = item.BaseParamSpecial;
                    if (hqParams.Count > 0)
                    {
                        var hqValues = item.BaseParamValueSpecial;
                        int hqIdx = 0;
                        foreach (var hqParam in hqParams)
                        {
                            var hqParamId = hqParam.RowId;
                            var hqParamValue = hqValues[hqIdx];

                            if (hqParamId == 70)
                                craftsmanship += hqParamValue;
                            else if (hqParamId == 71)
                                control += hqParamValue;
                            else if (hqParamId == 11)
                                cp += hqParamValue;

                            hqIdx++;
                        }
                    }
                }

                for (int m = 0; m < 5; m++)
                {
                    var materiaId = gearItem.Materia[m];
                    if (materiaId == 0)
                        continue;

                    if (!materiaSheet.TryGetRow(materiaId, out var materia))
                        continue;

                    var baseParamId = materia.BaseParam.RowId;
                    if (baseParamId == 70)
                        craftsmanship += materia.Value[gearItem.MateriaGrades[m]];
                    else if (baseParamId == 71)
                        control += materia.Value[gearItem.MateriaGrades[m]];
                    else if (baseParamId == 11)
                        cp += materia.Value[gearItem.MateriaGrades[m]];
                }
            }

            var manipulation = IsManipulationUnlocked(jobId);
            var isSpecialist = gearset->Items[13].ItemId != 0;

            return new GameStateBuilder.PlayerStats(
                Craftsmanship: craftsmanship,
                Control: control,
                CP: cp,
                Level: 100,
                Manipulation: manipulation,
                Specialist: isSpecialist,
                SplendorCosmic: false
            );
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[GearsetStatsReader] Failed to calculate stats from gearset: {ex.Message}");
            return null;
        }
    }

    private static bool IsManipulationUnlocked(uint jobId)
    {
        try
        {
            var manipulationQuestId = jobId switch
            {
                8 => 67979u,   // CRP
                9 => 68153u,   // BSM
                10 => 68132u,  // ARM
                11 => 68137u,  // GSM
                12 => 68147u,  // LTW
                13 => 67969u,  // WVR
                14 => 67974u,  // ALC
                15 => 68142u,  // CUL
                _ => 0u
            };

            if (manipulationQuestId == 0)
                return false;

            return QuestManager.IsQuestComplete(manipulationQuestId);
        }
        catch
        {
            return false;
        }
    }
    
    private static ItemFood? GetItemConsumableProperties(Item item, bool hq)
    {
        if (!item.ItemAction.IsValid)
            return null;
        var action = item.ItemAction.Value;
        var actionParams = hq ? action.DataHQ : action.Data;
        if (actionParams[0] is not 48 and not 49)
            return null;
        return Dalamud.GameData.GetExcelSheet<ItemFood>()?.GetRow(actionParams[1]);
    }
    
    public static (int craftsmanship, int control, int cp) CalculateConsumableBonus(uint itemId, bool isHQ, int baseCraftsmanship, int baseControl, int baseCP)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet == null || !itemSheet.TryGetRow(itemId, out var item))
            return (0, 0, 0);
        
        var food = GetItemConsumableProperties(item, isHQ);
        if (food == null)
            return (0, 0, 0);
        
        int craftBonus = 0;
        int controlBonus = 0;
        int cpBonus = 0;
        
        foreach (var p in food.Value.Params)
        {
            if (p.BaseParam.RowId == 70) // Craftsmanship
            {
                var val = isHQ ? p.ValueHQ : p.Value;
                var max = isHQ ? p.MaxHQ : p.Max;
                if (p.IsRelative)
                    craftBonus = Math.Min(max, baseCraftsmanship * val / 100);
                else
                    craftBonus = val;
            }
            else if (p.BaseParam.RowId == 71) // Control
            {
                var val = isHQ ? p.ValueHQ : p.Value;
                var max = isHQ ? p.MaxHQ : p.Max;
                if (p.IsRelative)
                    controlBonus = Math.Min(max, baseControl * val / 100);
                else
                    controlBonus = val;
            }
            else if (p.BaseParam.RowId == 11) // CP
            {
                var val = isHQ ? p.ValueHQ : p.Value;
                var max = isHQ ? p.MaxHQ : p.Max;
                if (p.IsRelative)
                    cpBonus = Math.Min(max, baseCP * val / 100);
                else
                    cpBonus = val;
            }
        }
        
        return (craftBonus, controlBonus, cpBonus);
    }
    
    public static GameStateBuilder.PlayerStats ApplyConsumablesToStats(GameStateBuilder.PlayerStats baseStats, RecipeCraftSettings? settings)
    {
        var foodId     = settings?.FoodItemId;
        var foodHQ     = settings?.FoodHQ ?? false;
        var medicineId = settings?.MedicineItemId;
        var medicineHQ = settings?.MedicineHQ ?? false;
        return ApplyConsumablesToStats(baseStats, foodId, foodHQ, medicineId, medicineHQ);
    }

    public static GameStateBuilder.PlayerStats ApplyConsumablesToStats(GameStateBuilder.PlayerStats baseStats, uint? foodId, bool foodHQ, uint? medicineId, bool medicineHQ)
    {
        var craftsmanship = baseStats.Craftsmanship;
        var control = baseStats.Control;
        var cp = baseStats.CP;

        if (foodId.HasValue)
        {
            var (craftBonus, controlBonus, cpBonus) = CalculateConsumableBonus(foodId.Value, foodHQ, craftsmanship, control, cp);
            craftsmanship += craftBonus;
            control += controlBonus;
            cp += cpBonus;
        }

        if (medicineId.HasValue)
        {
            var (craftBonus, controlBonus, cpBonus) = CalculateConsumableBonus(medicineId.Value, medicineHQ, craftsmanship, control, cp);
            craftsmanship += craftBonus;
            control += controlBonus;
            cp += cpBonus;
        }

        return new GameStateBuilder.PlayerStats(
            Craftsmanship: craftsmanship,
            Control: control,
            CP: cp,
            Level: baseStats.Level,
            Manipulation: baseStats.Manipulation,
            Specialist: baseStats.Specialist,
            SplendorCosmic: baseStats.SplendorCosmic
        );
    }
}
