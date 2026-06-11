using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.STD;
using GatherBuddy.Deimos.Data;

namespace GatherBuddy.Deimos.Scheduling;

/// picks which mission to grab next off the board, per config (craft-only for now).
/// by default prefers a mission that grants research XP the job still needs.
internal static class MissionSelector
{
    public static unsafe uint PickCraft(AgentWKSMission* agent, uint job, DeimosConfig config)
        => PickCraftScored(agent, job, config, out _);

    /// best candidate for the job + its preference score (board must be on the job's tab)
    public static unsafe uint PickCraftScored(AgentWKSMission* agent, uint job, DeimosConfig config, out int bestScore)
    {
        var needed = Wks.NeededResearchTypes(job);
        var level  = Dalamud.Objects.LocalPlayer?.Level ?? 0;
        var tier   = config.Mode == DeimosMode.Leveling ? LevelTier(level) : 0u;

        var board = OnBoard(agent, config);

        // leveling: when higher ranks are still locked, run incomplete curated missions of the top
        // available rank - completing those is what unlocks the next bracket
        var unlockRank = 0u;
        if (config.Mode == DeimosMode.Leveling && board.Count > 0)
        {
            var expected = level >= 90 ? 3u : level >= 50 ? 2u : 1u;
            var maxRank = board.Max(id => MissionData.Get(id)?.Rank ?? 0);
            if (maxRank < expected)
                unlockRank = maxRank;
        }

        uint first = 0, best = 0;
        bestScore = 0;

        foreach (var id in board)
        {
            if (!MissionData.TryGet(id, out var info))
                continue;
            if (!info.IsCraftOnly || !info.Jobs.Contains(job))
                continue;
            if (config.SkipExpertCrafts && info.IsExpert)
                continue;
            if (!config.IncludeMastery && info.Attributes.HasFlag(MissionAttributes.Master))
                continue;
            // empty enabled set = run anything
            if (config.EnabledMissions.Count > 0 && !config.EnabledMissions.Contains(id))
                continue;
            if (config.Mode == DeimosMode.MissionGold && Wks.IsMissionGolded(id))
                continue;
            if (tier != 0 && info.Level != tier)
                continue;
            if (unlockRank != 0 && (info.Rank != unlockRank || Wks.IsMissionCompleted(id)))
                continue;

            if (first == 0)
                first = id;

            var score = info.RelicXpInfo.Where(x => needed.Contains(x.Key)).Sum(x => x.Value);
            if (config.Mode == DeimosMode.Leveling && CosmicLists.IsQuickLevel(id))
                score += 10000; // curated leveling picks first

            if (score > bestScore)
            {
                bestScore = score;
                best      = id;
            }
        }

        return best != 0 ? best : first;
    }

    /// any basic craft mission for the job, ignoring filters - reroll fodder
    public static unsafe uint AnyCraft(AgentWKSMission* agent, uint job)
    {
        StdVector<AgentWKSMission.MissionEntry> basics = default;
        if (!agent->GetBasicMissions(&basics))
            return 0;

        foreach (var entry in basics)
            if (MissionData.TryGet(entry.MissionUnitId, out var info) && info.IsCraftOnly && info.Jobs.Contains(job))
                return entry.MissionUnitId;

        return 0;
    }

    private static unsafe List<uint> OnBoard(AgentWKSMission* agent, DeimosConfig config)
    {
        var ids = new List<uint>();

        StdVector<AgentWKSMission.MissionEntry> basics = default;
        if (agent->GetBasicMissions(&basics))
            foreach (var entry in basics)
                Add(ids, entry.MissionUnitId);

        if (config.IncludeProvisionals)
        {
            StdVector<AgentWKSMission.MissionEntry> provisionals = default;
            if (agent->GetProvisionalMissions(&provisionals))
                foreach (var entry in provisionals)
                    Add(ids, entry.MissionUnitId);
        }

        if (config.IncludeCriticals)
        {
            StdVector<AgentWKSMission.MissionEntry> criticals = default;
            if (agent->GetCriticalMissions(&criticals))
                foreach (var entry in criticals)
                    Add(ids, entry.MissionUnitId);
        }

        if (config.IncludeMastery)
        {
            StdVector<AgentWKSMission.MissionEntry> mastery = default;
            if (Wks.TryGetMasterMissions(agent, &mastery))
                foreach (var entry in mastery)
                    Add(ids, entry.MissionUnitId);
        }

        return ids;
    }

    private static void Add(List<uint> ids, uint id)
    {
        if (!ids.Contains(id))
            ids.Add(id);
    }

    // 10/50/90 leveling brackets; 0 = capped, no tier filter
    private static uint LevelTier(int level) => level switch
    {
        >= 100 => 0,
        >= 90  => 90,
        >= 50  => 50,
        _      => 10,
    };
}
