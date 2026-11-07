using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using GatherBuddy.Vulcan;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System;

namespace GatherBuddy.Crafting;

public static unsafe class GearsetStatsReader
{
    private const uint CraftsmanshipParamId = 70;
    private const uint ControlParamId = 71;
    private const uint CpParamId = 11;
    private const int CraftsmanshipIndex = 0;
    private const int ControlIndex = 1;
    private const int CpIndex = 2;
    private const int StatCount = 3;
    private const int MateriaSlotCount = 5;
    private const int GearsetItemCount = 14;
    private const int SpecialistSlotIndex = 13;
    private const int BaseCp = 180;
    private const int MaxEquipSlotCategoryId = 22;

    public static string RefreshGearsetFromCurrentEquipped(uint jobId)
    {
        try
        {
            var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
            if (currentJob != jobId)
            {
                var message = "Switch to the selected job before updating its saved gearset.";
                GatherBuddy.Log.Debug($"[GearsetStatsReader] {message} RequestedJob={jobId}, CurrentJob={currentJob}");
                return message;
            }

            var gearsetModule = RaptureGearsetModule.Instance();
            if (gearsetModule == null)
            {
                GatherBuddy.Log.Warning("[GearsetStatsReader] Could not update gearset: gearset module is unavailable.");
                return "Failed to update the saved gearset: gearset module unavailable.";
            }

            if (!TryResolveRefreshTarget(gearsetModule, jobId, out var gearsetIndex, out var usedCurrentGearset))
            {
                var message = "No saved gearset was found for the selected job.";
                GatherBuddy.Log.Debug($"[GearsetStatsReader] {message} JobId={jobId}");
                return message;
            }

            var updateResult = gearsetModule->UpdateGearset(gearsetIndex);
            if (updateResult < 0)
            {
                GatherBuddy.Log.Warning($"[GearsetStatsReader] Failed to update gearset {gearsetIndex} for job {jobId}. Result={updateResult}");
                return $"Failed to update saved gearset {gearsetIndex}.";
            }

            var targetDescription = usedCurrentGearset
                ? $"active saved gearset {gearsetIndex}"
                : $"first matching saved gearset {gearsetIndex}";
            var successMessage = $"Updated {targetDescription} from currently equipped items.";
            GatherBuddy.Log.Information($"[GearsetStatsReader] {successMessage}");
            return successMessage;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[GearsetStatsReader] Failed to update saved gearset for job {jobId}: {ex.Message}");
            return "Failed to update the saved gearset.";
        }
    }

    private static GameStateBuilder.PlayerStats? ReadFromCurrentlyEquipped(uint jobId)
    {
        try
        {
            var craftsmanship = 0;
            var control = 0;
            var cp = BaseCp;

            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            var materiaSheet = Dalamud.GameData.GetExcelSheet<Materia>();

            if (itemSheet == null || materiaSheet == null)
            {
                GatherBuddy.Log.Debug("[GearsetStatsReader] Item or Materia sheet is null");
                return null;
            }

            var inventoryMgr = InventoryManager.Instance();
            if (inventoryMgr == null)
            {
                return null;
            }

            var equippedContainer = inventoryMgr->GetInventoryContainer(InventoryType.EquippedItems);
            if (equippedContainer == null || equippedContainer->Size == 0)
            {
                return null;
            }

            for (var i = 0; i < equippedContainer->Size; i++)
            {
                var inventoryItem = equippedContainer->Items + i;
                if (inventoryItem->ItemId == 0)
                {
                    continue;
                }

                var actualItemId = inventoryItem->ItemId;
                var isHQ = inventoryItem->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);

                if (!itemSheet.TryGetRow(actualItemId, out var item))
                {
                    continue;
                }

                var baseStats = new int[StatCount];
                var meldStats = new int[StatCount];

                AccumulateBaseStats(item, isHQ, baseStats);

                for (var m = 0; m < MateriaSlotCount; m++)
                {
                    var materiaId = inventoryItem->Materia[m];
                    if (materiaId == 0 || !materiaSheet.TryGetRow(materiaId, out var materia))
                    {
                        continue;
                    }

                    AccumulateMateriaStats(materia, inventoryItem->MateriaGrades[m], meldStats);
                }

                craftsmanship += CalculateEffectiveItemStat(item, CraftsmanshipParamId, baseStats[CraftsmanshipIndex], meldStats[CraftsmanshipIndex]);
                control += CalculateEffectiveItemStat(item, ControlParamId, baseStats[ControlIndex], meldStats[ControlIndex]);
                cp += CalculateEffectiveItemStat(item, CpParamId, baseStats[CpIndex], meldStats[CpIndex]);
            }

            var manipulation = IsManipulationUnlocked(jobId);
            var isSpecialist = equippedContainer->Size > SpecialistSlotIndex && (equippedContainer->Items + SpecialistSlotIndex)->ItemId != 0;

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

    private static bool TryResolveRefreshTarget(RaptureGearsetModule* gearsetModule, uint jobId, out int gearsetIndex, out bool usedCurrentGearset)
    {
        gearsetIndex = -1;
        usedCurrentGearset = false;

        var currentGearsetIndex = gearsetModule->CurrentGearsetIndex;
        if (IsMatchingGearset(gearsetModule, currentGearsetIndex, jobId))
        {
            gearsetIndex = currentGearsetIndex;
            usedCurrentGearset = true;
            return true;
        }

        return TryResolveExistingGearsetIndex(gearsetModule, jobId, out gearsetIndex);
    }

    internal static bool TryResolveExistingGearsetIndex(RaptureGearsetModule* gearsetModule, uint jobId, out int gearsetIndex)
        => TryResolveExistingGearsetIndex(gearsetModule, jobId, null, out gearsetIndex, out _);

    internal static bool TryResolveExistingGearsetIndex(RaptureGearsetModule* gearsetModule, uint jobId, string? preferredName,
        out int gearsetIndex, out string gearsetName)
    {
        var fallbackIndex = -1;
        for (var i = 0; i < 100; i++)
        {
            if (!IsMatchingGearset(gearsetModule, i, jobId))
            {
                continue;
            }

            if (fallbackIndex < 0)
            {
                fallbackIndex = i;
            }

            var candidateName = gearsetModule->Entries[i].NameString;
            if (string.IsNullOrWhiteSpace(preferredName) || !string.Equals(candidateName, preferredName, StringComparison.Ordinal))
            {
                continue;
            }

            gearsetIndex = i;
            gearsetName = candidateName;
            return true;
        }

        gearsetIndex = fallbackIndex;
        gearsetName = fallbackIndex >= 0 ? gearsetModule->Entries[fallbackIndex].NameString : string.Empty;
        return fallbackIndex >= 0;
    }

    private static bool IsMatchingGearset(RaptureGearsetModule* gearsetModule, int gearsetIndex, uint jobId)
    {
        if (gearsetIndex < 0 || gearsetIndex >= 100)
        {
            return false;
        }

        var gearset = gearsetModule->Entries[gearsetIndex];
        return (gearset.Flags & RaptureGearsetModule.GearsetFlag.Exists) != 0 && gearset.ClassJob == jobId;
    }

    private static void AccumulateBaseStats(Item item, bool isHighQuality, Span<int> baseStats)
    {
        var baseParams = item.BaseParam;
        var baseParamValues = item.BaseParamValue;

        var baseParamIndex = 0;
        foreach (var paramRef in baseParams)
        {
            AddStatValue(baseStats, paramRef.RowId, baseParamValues[baseParamIndex]);
            baseParamIndex++;
        }

        if (!isHighQuality || item.BaseParamSpecial.Count == 0)
        {
            return;
        }

        var hqValues = item.BaseParamValueSpecial;
        var hqParamIndex = 0;
        foreach (var paramRef in item.BaseParamSpecial)
        {
            AddStatValue(baseStats, paramRef.RowId, hqValues[hqParamIndex]);
            hqParamIndex++;
        }
    }

    private static void AccumulateMateriaStats(Materia materia, byte grade, Span<int> meldStats)
    {
        AddStatValue(meldStats, materia.BaseParam.RowId, materia.Value[grade]);
    }

    private static void AddStatValue(Span<int> stats, uint paramId, int value)
    {
        if (value == 0 || !TryGetStatIndex(paramId, out var statIndex))
        {
            return;
        }

        stats[statIndex] += value;
    }

    private static bool TryGetStatIndex(uint paramId, out int statIndex)
    {
        statIndex = paramId switch
        {
            CraftsmanshipParamId => CraftsmanshipIndex,
            ControlParamId => ControlIndex,
            CpParamId => CpIndex,
            _ => -1
        };
        return statIndex >= 0;
    }

    private static int CalculateEffectiveItemStat(Item item, uint paramId, int baseValue, int meldedValue)
    {
        var uncappedValue = baseValue + meldedValue;
        if (uncappedValue == 0)
        {
            return 0;
        }

        if (!TryGetItemStatCap(item, paramId, baseValue, out var cap))
        {
            return uncappedValue;
        }

        return Math.Min(uncappedValue, cap);
    }

    private static bool TryGetItemStatCap(Item item, uint paramId, int baseValue, out int cap)
    {
        cap = 0;

        var slotCategoryId = (int)item.EquipSlotCategory.RowId;
        if (slotCategoryId <= 0 || slotCategoryId > MaxEquipSlotCategoryId)
        {
            GatherBuddy.Log.Debug($"[GearsetStatsReader] Missing slot cap data for item {item.RowId} and stat {paramId}: slot category {slotCategoryId} is out of range.");
            return false;
        }

        var levelStatValue = GetItemLevelStat(item, paramId);
        if (levelStatValue <= 0)
        {
            cap = baseValue;
            return true;
        }

        var baseParamSheet = Dalamud.GameData.GetExcelSheet<RawRow>(name: "BaseParam");
        if (baseParamSheet == null)
        {
            GatherBuddy.Log.Debug($"[GearsetStatsReader] Missing BaseParam sheet while calculating cap for item {item.RowId} and stat {paramId}.");
            return false;
        }

        if (!baseParamSheet.TryGetRow(paramId, out var baseParamRow))
        {
            GatherBuddy.Log.Debug($"[GearsetStatsReader] Missing BaseParam row {paramId} while calculating cap for item {item.RowId}.");
            return false;
        }

        var slotModifier = baseParamRow.ReadInt16Column(slotCategoryId + 3);
        if (slotModifier <= 0)
        {
            GatherBuddy.Log.Debug($"[GearsetStatsReader] Missing slot modifier for item {item.RowId}, stat {paramId}, slot category {slotCategoryId}.");
            return false;
        }

        cap = Math.Max(
            baseValue,
            (int)Math.Round(levelStatValue * slotModifier / 1000d, MidpointRounding.AwayFromZero));
        return true;
    }

    private static int GetItemLevelStat(Item item, uint paramId)
    {
        var levelItem = item.LevelItem.Value;
        return paramId switch
        {
            CraftsmanshipParamId => levelItem.Craftsmanship,
            ControlParamId => levelItem.Control,
            CpParamId => levelItem.CP,
            _ => 0
        };
    }

    public static GameStateBuilder.PlayerStats? ReadGearsetStatsForJob(uint jobId)
    {
        try
        {
            var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;

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
            {
                return null;
            }

            fixed (RaptureGearsetModule.GearsetEntry* entries = gearsetModule->Entries)
            {
                for (var i = 0; i < 100; i++)
                {
                    if ((entries[i].Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0)
                    {
                        continue;
                    }

                    if (entries[i].ClassJob != jobId)
                    {
                        continue;
                    }

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
            var cp = BaseCp;

            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            var materiaSheet = Dalamud.GameData.GetExcelSheet<Materia>();

            if (itemSheet == null || materiaSheet == null)
            {
                GatherBuddy.Log.Debug("[GearsetStatsReader] Item or Materia sheet is null");
                return null;
            }

            for (var i = 0; i < GearsetItemCount; i++)
            {
                var gearItem = gearset->Items[i];
                if (gearItem.ItemId == 0)
                {
                    continue;
                }

                var actualItemId = gearItem.ItemId % 1000000;
                var isHQ = gearItem.ItemId >= 1000000;
                if (!itemSheet.TryGetRow(actualItemId, out var item))
                {
                    continue;
                }

                var baseStats = new int[StatCount];
                var meldStats = new int[StatCount];

                AccumulateBaseStats(item, isHQ, baseStats);

                for (var m = 0; m < MateriaSlotCount; m++)
                {
                    var materiaId = gearItem.Materia[m];
                    if (materiaId == 0 || !materiaSheet.TryGetRow(materiaId, out var materia))
                    {
                        continue;
                    }

                    AccumulateMateriaStats(materia, gearItem.MateriaGrades[m], meldStats);
                }

                craftsmanship += CalculateEffectiveItemStat(item, CraftsmanshipParamId, baseStats[CraftsmanshipIndex], meldStats[CraftsmanshipIndex]);
                control += CalculateEffectiveItemStat(item, ControlParamId, baseStats[ControlIndex], meldStats[ControlIndex]);
                cp += CalculateEffectiveItemStat(item, CpParamId, baseStats[CpIndex], meldStats[CpIndex]);
            }

            var manipulation = IsManipulationUnlocked(jobId);
            var isSpecialist = gearset->Items[SpecialistSlotIndex].ItemId != 0;

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
            {
                return false;
            }

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
        {
            return null;
        }

        var action = item.ItemAction.Value;
        var actionParams = hq ? action.DataHQ : action.Data;
        if (actionParams[0] is not 48 and not 49)
        {
            return null;
        }

        return Dalamud.GameData.GetExcelSheet<ItemFood>()?.GetRow(actionParams[1]);
    }

    public static (int craftsmanship, int control, int cp) CalculateConsumableBonus(uint itemId, bool isHQ, int baseCraftsmanship, int baseControl, int baseCP)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet == null || !itemSheet.TryGetRow(itemId, out var item))
        {
            return (0, 0, 0);
        }

        var food = GetItemConsumableProperties(item, isHQ);
        if (food == null)
        {
            return (0, 0, 0);
        }

        var craftBonus = 0;
        var controlBonus = 0;
        var cpBonus = 0;

        foreach (var p in food.Value.Params)
        {
            if (p.BaseParam.RowId == 70) // Craftsmanship
            {
                var val = isHQ ? p.ValueHQ : p.Value;
                var max = isHQ ? p.MaxHQ : p.Max;
                if (p.IsRelative)
                {
                    craftBonus = Math.Min(max, baseCraftsmanship * val / 100);
                }
                else
                {
                    craftBonus = val;
                }
            }
            else if (p.BaseParam.RowId == 71) // Control
            {
                var val = isHQ ? p.ValueHQ : p.Value;
                var max = isHQ ? p.MaxHQ : p.Max;
                if (p.IsRelative)
                {
                    controlBonus = Math.Min(max, baseControl * val / 100);
                }
                else
                {
                    controlBonus = val;
                }
            }
            else if (p.BaseParam.RowId == 11) // CP
            {
                var val = isHQ ? p.ValueHQ : p.Value;
                var max = isHQ ? p.MaxHQ : p.Max;
                if (p.IsRelative)
                {
                    cpBonus = Math.Min(max, baseCP * val / 100);
                }
                else
                {
                    cpBonus = val;
                }
            }
        }

        return (craftBonus, controlBonus, cpBonus);
    }

    public static GameStateBuilder.PlayerStats ApplyConsumablesToStats(GameStateBuilder.PlayerStats baseStats, RecipeCraftSettings? settings)
    {
        var foodId = settings?.FoodItemId;
        var foodHQ = settings?.FoodHQ ?? false;
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
