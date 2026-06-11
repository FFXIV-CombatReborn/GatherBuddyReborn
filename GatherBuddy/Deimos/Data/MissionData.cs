using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Deimos.Data;

/// cosmic mission data built once from game sheets
public static class MissionData
{
    private static readonly Dictionary<uint, CosmicInfo> _missions = new();
    private static readonly Dictionary<uint, uint>       _scores   = new();
    private static bool _built;

    public static IReadOnlyDictionary<uint, CosmicInfo> Missions
    {
        get
        {
            EnsureBuilt();
            return _missions;
        }
    }

    public static bool TryGet(uint missionId, out CosmicInfo info)
    {
        EnsureBuilt();
        return _missions.TryGetValue(missionId, out info!);
    }

    public static CosmicInfo? Get(uint missionId)
    {
        EnsureBuilt();
        return _missions.TryGetValue(missionId, out var info) ? info : null;
    }

    public static void EnsureBuilt()
    {
        if (_built)
            return;

        _built = true;
        try
        {
            Build();
        }
        catch (Exception ex)
        {
            DeimosLog.Error($"Mission data build failed: {ex}");
        }
    }

    private static void Build()
    {
        LoadScores();

        var missionSheet = Dalamud.GameData.GetExcelSheet<WKSMissionUnit>();
        var todoSheet    = Dalamud.GameData.GetExcelSheet<WKSMissionToDo>();
        var recipeSheet  = Dalamud.GameData.GetExcelSheet<Recipe>();
        var itemInfo     = Dalamud.GameData.GetExcelSheet<WKSItemInfo>();
        var rewardSheet  = Dalamud.GameData.GetExcelSheet<WKSMissionReward>();

        foreach (var entry in missionSheet)
        {
            try
            {
                var info = BuildMission(entry, todoSheet, recipeSheet, itemInfo, rewardSheet);
                if (info != null)
                    _missions[entry.RowId] = info;
            }
            catch (Exception ex)
            {
                DeimosLog.Verbose($"Skipped mission {entry.RowId}: {ex.Message}");
            }
        }

        LinkSequences();
        ApplyScores();

        DeimosLog.Info($"Built {_missions.Count} cosmic missions.");
    }

    private static CosmicInfo? BuildMission(
        WKSMissionUnit entry,
        Lumina.Excel.ExcelSheet<WKSMissionToDo> todoSheet,
        Lumina.Excel.ExcelSheet<Recipe> recipeSheet,
        Lumina.Excel.ExcelSheet<WKSItemInfo> itemInfo,
        Lumina.Excel.ExcelSheet<WKSMissionReward> rewardSheet)
    {
        var keyId = entry.RowId;
        if (keyId == 0)
            return null;
        if (entry.ClassJobCategory[0].RowId == 0)
            return null;

        var crafts_Main   = new Dictionary<ushort, CraftingInfo>();
        var crafts_Pre    = new Dictionary<ushort, CraftingInfo>();
        var gathering_Min = new Dictionary<uint, int>();
        var jobs          = new List<uint>();
        var relicXp       = new Dictionary<int, int>();
        var isExpert      = false;
        var isCollectable = false;

        var missionName = entry.Name.ExtractText().Replace("<nbsp>", " ").Replace("<->", "");
        var missionToDo = todoSheet.GetRow(entry.MissionToDo[0].RowId);
        var timeLimit = entry.MissionTime;
        var gold      = entry.GoldStarRequirement;
        var silver    = entry.SilverStarRequirement;
        var bronze    = missionToDo.Unknown2;
        var rank  = (uint)entry.LevelGroup;
        uint level = rank switch { 1 => 10, 2 => 50, 3 => 90, >= 4 => 100, _ => 0 };
        var isCritical = entry.IsSpecialQuest;
        var previousMissionId = entry.LockedBehind.RowId;

        // jobs
        jobs.Add(entry.ClassJobCategory[0].RowId - 1);
        var job2 = entry.ClassJobCategory[1].RowId;
        if (job2 != 0)
            jobs.Add(job2 - 1);

        var timeAndWeather = entry.WKSMissionLotterySpecialCond.RowId;
        uint startTime = 0, endTime = 0;
        var weather = CosmicWeather.None;
        if (!WeatherConditions.Contains(timeAndWeather))
        {
            var timeRow = entry.WKSMissionLotterySpecialCond.Value;
            startTime = timeRow.StartTimeHour;
            endTime   = timeRow.EndTimeHour;
        }
        else
        {
            weather = timeAndWeather switch
            {
                13 => CosmicWeather.UmbralWind,
                14 => CosmicWeather.MoonDust,
                15 => CosmicWeather.Clouds,
                16 => CosmicWeather.Rain,
                23 => CosmicWeather.ClearSkies,
                24 => CosmicWeather.FairSkies,
                _  => CosmicWeather.None,
            };
        }

        uint territoryId = keyId switch
        {
            < 545  => CosmicZone.SinusArdorum,
            < 1040 => CosmicZone.Phaenna,
            < 1370 => CosmicZone.Oizys,
            _      => CosmicZone.Auxesia,
        };

        var marker = missionToDo.MapMarker;
        var mapFlag = new Vector2(marker.Value.X - 1024, marker.Value.Y - 1024);
        var radius  = (int)marker.Value.Radius;
        mapFlag = keyId switch
        {
            1272 => new Vector2(-340, 870),
            1264 => new Vector2(-573, 3),
            1296 => new Vector2(-514, 232),
            1317 or 1318 or 1319 => new Vector2(mapFlag.X + 1, mapFlag.Y + 1),
            _ => mapFlag,
        };

        // attributes
        var attributes = MissionAttributes.None;
        if (jobs.Count > 1)
            attributes = jobs.Contains(18u)
                ? MissionAttributes.Craft | MissionAttributes.Fish
                : MissionAttributes.Craft | MissionAttributes.Gather;
        else if (CosmicJobs.Crafters.Any(jobs.Contains))
            attributes = MissionAttributes.Craft;
        else
        {
            attributes |= jobs.Contains(18u) ? MissionAttributes.Fish : MissionAttributes.Gather;
            attributes = missionToDo.WKSMissionText.RowId switch
            {
                103 => MissionAttributes.Gather | MissionAttributes.Limited,
                104 => MissionAttributes.Gather | MissionAttributes.ScoreTimeRemaining,
                105 => MissionAttributes.Gather,
                106 => MissionAttributes.Gather | MissionAttributes.ScoreChains,
                107 => MissionAttributes.Gather | MissionAttributes.ScoreGatherersBoon,
                108 => MissionAttributes.Gather | MissionAttributes.ScoreChains | MissionAttributes.ScoreGatherersBoon,
                109 or 111 => MissionAttributes.Gather | MissionAttributes.Collectables,
                110 => MissionAttributes.Gather | MissionAttributes.ReducedItems | MissionAttributes.ScoreTimeRemaining,
                112 => MissionAttributes.Gather | MissionAttributes.ReducedItems,
                113 => MissionAttributes.Fish | MissionAttributes.ScoreVariety | MissionAttributes.ScoreTimeRemaining,
                114 or 115 => MissionAttributes.Fish | MissionAttributes.ScoreTimeRemaining,
                116 => MissionAttributes.Fish | MissionAttributes.Limited | MissionAttributes.ScoreVariety,
                117 => MissionAttributes.Fish | MissionAttributes.Limited | MissionAttributes.ScoreLargestSize,
                118 => MissionAttributes.Fish | MissionAttributes.Limited | MissionAttributes.Collectables,
                119 or 121 => MissionAttributes.Fish,
                120 => MissionAttributes.Fish | MissionAttributes.ScoreLargestSize,
                122 => MissionAttributes.Fish | MissionAttributes.Collectables,
                139 => jobs.Contains(18u) ? MissionAttributes.Fish : MissionAttributes.Gather,
                141 => MissionAttributes.Fish,
                _ => MissionAttributes.None,
            };
        }

        attributes |= isCritical ? MissionAttributes.Critical : MissionAttributes.None;
        attributes |= weather != CosmicWeather.None ? MissionAttributes.ProvisionalWeather : MissionAttributes.None;
        attributes |= (startTime != 0 || endTime != 0) ? MissionAttributes.ProvisionalTimed : MissionAttributes.None;
        attributes |= previousMissionId != 0 ? MissionAttributes.ProvisionalSequential : MissionAttributes.None;

        const MissionAttributes provisionalMask = MissionAttributes.ProvisionalWeather
                                                | MissionAttributes.ProvisionalTimed
                                                | MissionAttributes.ProvisionalSequential;
        if (rank == 6 && (attributes & provisionalMask) == MissionAttributes.None)
            attributes |= MissionAttributes.Master;

        var tempActionId    = missionToDo.TemporaryAction.RowId;
        var tempActionCount = (uint)missionToDo.Unknown14;

        // crafter
        var wksRecipe = entry.WKSMissionRecipe;
        var wksRecipeRowId = wksRecipe.RowId;
        if (CosmicJobs.Crafters.Any(jobs.Contains))
        {
            var craftJob = jobs.First(CosmicJobs.Crafters.Contains);

            if (isCritical)
            {
                var requiredAmount = keyId > 535 ? 2 : 3;
                if (recipeSheet.TryGetRow(wksRecipe.Value.Recipe[0].RowId, out var recipeRow))
                {
                    var item = recipeRow.ItemResult;
                    crafts_Main[(ushort)recipeRow.RowId] = new CraftingInfo
                    {
                        ItemId         = item.RowId,
                        RecipeId       = wksRecipeRowId,
                        RequiredAmount = requiredAmount,
                        ItemName       = item.Value.Name.ExtractText(),
                        IconId         = item.Value.Icon,
                    };
                }
            }
            else
            {
                var recipeIds = new List<ushort>();
                for (var x = 2; x >= 0; x--)
                {
                    var rid = (ushort)wksRecipe.Value.Recipe[x].RowId;
                    if (rid != 0 && !recipeIds.Contains(rid))
                        recipeIds.Add(rid);
                }

                if (recipeIds.Count == 1)
                {
                    var rid = recipeIds[0];
                    var recipeRow = recipeSheet.GetRow(rid);
                    var amountNeeded = (int)missionToDo.RequiredItemQuantity[0];
                    if (amountNeeded == 0) amountNeeded = 1;
                    var reqItem  = recipeRow.Ingredient[0].RowId;
                    var reqAmt   = recipeRow.AmountIngredient[0];
                    var reqItem2 = recipeRow.Ingredient[1].RowId;
                    var reqAmt2  = recipeRow.AmountIngredient[1];
                    var expertMat = recipeRow.IsExpert;

                    var req = new Dictionary<uint, int> { [reqItem] = reqAmt };
                    if (reqItem2 != 0) req[reqItem2] = reqAmt2;

                    crafts_Main[rid] = new CraftingInfo
                    {
                        ItemId = recipeRow.ItemResult.RowId, RequiredAmount = amountNeeded, RecipeId = rid,
                        ExpertCraft = expertMat, RequiredItems = req,
                        IconId = recipeRow.ItemResult.Value.Icon, ItemName = recipeRow.ItemResult.Value.Name.ExtractText(),
                    };
                    isExpert |= expertMat;
                    isCollectable |= recipeRow.CollectableMetadataKey == 1;
                }
                else if (recipeIds.Count == 2)
                {
                    var rid = recipeIds[0];
                    var recipeRow = recipeSheet.GetRow(rid);
                    var amountNeeded = (int)missionToDo.RequiredItemQuantity[0];
                    if (amountNeeded == 0) amountNeeded = 1;
                    var reqItem = recipeRow.Ingredient[0].RowId;
                    var reqAmt  = recipeRow.AmountIngredient[0];
                    crafts_Main[rid] = new CraftingInfo
                    {
                        ItemId = recipeRow.ItemResult.RowId, RequiredAmount = amountNeeded, RecipeId = rid,
                        ExpertCraft = recipeRow.IsExpert, RequiredItems = new() { [reqItem] = reqAmt },
                        IconId = recipeRow.ItemResult.Value.Icon, ItemName = recipeRow.ItemResult.Value.Name.ExtractText(),
                    };

                    var preRid = recipeIds[1];
                    var preRow = recipeSheet.GetRow(preRid);
                    var crateId = preRow.Ingredient[0].RowId;
                    crafts_Pre[preRid] = new CraftingInfo
                    {
                        ItemId = preRow.ItemResult.RowId, RequiredAmount = reqAmt, RecipeId = preRid,
                        ExpertCraft = preRow.IsExpert, RequiredItems = new() { [crateId] = reqAmt },
                        IconId = preRow.ItemResult.Value.Icon, ItemName = preRow.ItemResult.Value.Name.ExtractText(),
                    };
                    isExpert |= recipeRow.IsExpert || preRow.IsExpert;
                    isCollectable |= recipeRow.CollectableMetadataKey == 1 || preRow.CollectableMetadataKey == 1;
                }
                else if (recipeIds.Count == 3)
                {
                    for (var i = 0; i < recipeIds.Count; i++)
                    {
                        var rid = recipeIds[i];
                        var recipeRow = recipeSheet.GetRow(rid);
                        var amountNeeded = (int)missionToDo.RequiredItemQuantity[i];
                        if (amountNeeded == 0) amountNeeded = 1;
                        var reqItem = recipeRow.Ingredient[0].RowId;
                        var reqAmt  = recipeRow.AmountIngredient[0];
                        crafts_Main[rid] = new CraftingInfo
                        {
                            ItemId = recipeRow.ItemResult.RowId, RequiredAmount = amountNeeded, RecipeId = rid,
                            ExpertCraft = recipeRow.IsExpert, RequiredItems = new() { [reqItem] = reqAmt },
                            IconId = recipeRow.ItemResult.Value.Icon, ItemName = recipeRow.ItemResult.Value.Name.ExtractText(),
                        };
                        isExpert |= recipeRow.IsExpert;
                        isCollectable |= recipeRow.CollectableMetadataKey == 1;
                    }
                }

                // missions with no main item: promote a pre-craft
                if (crafts_Main.Count == 0)
                {
                    foreach (var pre in crafts_Pre.ToList())
                    {
                        pre.Value.RequiredAmount = 1;
                        crafts_Main.Add(pre.Key, pre.Value);
                        crafts_Pre.Remove(pre.Key);
                    }
                }
            }

            if (isExpert)      attributes |= MissionAttributes.ExpertCraft;
            if (isCollectable) attributes |= MissionAttributes.Collectables;
        }

        // gatherer
        if (CosmicJobs.Gatherers.Any(jobs.Contains))
        {
            for (var i = 0; i < 3; i++)
            {
                if (missionToDo.RequiredItem[i].RowId == 0)
                    continue;
                var minAmount  = (int)missionToDo.RequiredItemQuantity[i];
                var infoItemId = itemInfo.GetRow(missionToDo.RequiredItem[i].RowId).Item.RowId;
                gathering_Min.TryAdd(infoItemId, minAmount);
            }
        }

        if (tempActionId == 42060)
        {
            if (attributes.HasFlag(MissionAttributes.ScoreGatherersBoon)) attributes |= MissionAttributes.GreaterReachBoon;
            else if (attributes.HasFlag(MissionAttributes.ScoreChains))   attributes |= MissionAttributes.GreaterReachChain;
            else                                                          attributes |= MissionAttributes.GreaterReachGather;
        }

        // fisher
        int fishVariety = 0, fishAmount = 0;
        if (jobs.Contains(18u))
        {
            if (missionToDo.Unknown9 != 0)
                fishVariety = missionToDo.Unknown9;
            else if (missionToDo.Unknown17 != 0)
                fishAmount = missionToDo.Unknown17;
        }

        // rewards
        var reward = rewardSheet.GetRow(keyId);
        var cosmo   = reward.CosmoCredits;
        var lunar   = reward.PlanetCredits;
        var dronebit = reward.BaseDronebits;
        var expMod1 = (uint)reward.ExpModifier[0];
        var expMod2 = (uint)reward.ExpModifier[1];
        var expMod3 = (uint)reward.ExpModifier[2];
        for (var i = 0; i < 3; i++)
        {
            var kind = reward.TypeIndex[i];
            var amt  = reward.ResearchReward[i];
            if (kind != 0)
                relicXp[kind] = amt;
        }
        uint rewardItemId = 0, rewardItemAmount = 0;
        if (reward.ItemCount != 0)
        {
            rewardItemId     = reward.ItemCount;
            rewardItemAmount = reward.ItemCount;
        }

        return new CosmicInfo
        {
            Name              = missionName,
            Jobs              = jobs,
            ToDoId            = missionToDo.RowId,
            Rank              = rank,
            Level             = level,
            Attributes        = attributes,
            Weather           = weather,
            StartTime         = startTime,
            EndTime           = endTime,
            CosmoCredit       = cosmo,
            LunarCredit       = lunar,
            PreviousMissionId = previousMissionId,
            RelicXpInfo       = relicXp,
            BronzeScore       = bronze,
            SilverScore       = silver,
            GoldScore         = gold,
            ExpModifier_1     = expMod1,
            ExpModifier_2     = expMod2,
            ExpModifier_3     = expMod3,
            RewardItem        = rewardItemId,
            RewardItemAmount  = rewardItemAmount,
            DronebitReward    = dronebit,
            MapPosition       = mapFlag,
            Radius            = radius,
            TerritoryId       = territoryId,
            MarkerId          = marker.RowId,
            Gathering_Min     = gathering_Min,
            Fish_AmountRequired = fishAmount,
            Fish_VarietyAmount  = fishVariety,
            Crafts_Main       = crafts_Main,
            Crafts_Pre        = crafts_Pre,
            IsExpert          = isExpert,
            TemporaryActionId = tempActionId,
            TemporaryActionCount = tempActionCount,
        };
    }

    // weather-based WKSMissionLotterySpecialCond row ids (vs time-based)
    private static readonly HashSet<uint> WeatherConditions = [13, 14, 15, 16, 23, 24];

    private static void LinkSequences()
    {
        foreach (var (missionId, info) in _missions)
        {
            if (info.PreviousMissionId == missionId)
                continue;
            if (_missions.TryGetValue(info.PreviousMissionId, out var prev))
            {
                info.SequenceMissions_Previous.Add(info.PreviousMissionId);
                prev.SequenceMissions_Next.Add(missionId);
            }
        }

        foreach (var (_, info) in _missions)
        {
            var current = info;
            while (current.PreviousMissionId != 0 && _missions.TryGetValue(current.PreviousMissionId, out var prev))
            {
                if (!info.SequenceMissions_Previous.Contains(current.PreviousMissionId))
                    info.SequenceMissions_Previous.Add(current.PreviousMissionId);
                current = prev;
            }

            current = info;
            while (current.SequenceMissions_Next.Count > 0)
            {
                var nextId = current.SequenceMissions_Next[0];
                if (!_missions.TryGetValue(nextId, out var next))
                    break;
                if (!info.SequenceMissions_Next.Contains(nextId))
                    info.SequenceMissions_Next.Add(nextId);
                current = next;
            }
        }
    }

    private static void ApplyScores()
    {
        foreach (var (id, info) in _missions)
            info.ClassScore = _scores.TryGetValue(id, out var score) ? score : 0;
    }

    private static void LoadScores()
    {
        _scores.Clear();
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("GatherBuddy.Deimos.Resources.MissionScores.csv");
            if (stream == null)
            {
                DeimosLog.Warning("MissionScores.csv resource not found.");
                return;
            }

            using var reader = new StreamReader(stream);
            var header = true;
            while (reader.ReadLine() is { } line)
            {
                if (header) { header = false; continue; }
                var parts = line.Split(',');
                if (parts.Length >= 4
                 && uint.TryParse(parts[0].Trim(), out var id)
                 && uint.TryParse(parts[3].Trim(), out var score))
                    _scores[id] = score;
            }
        }
        catch (Exception ex)
        {
            DeimosLog.Error($"Failed to load mission scores: {ex.Message}");
        }
    }
}
