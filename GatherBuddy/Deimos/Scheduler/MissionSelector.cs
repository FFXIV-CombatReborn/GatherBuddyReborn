using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.STD;
using GatherBuddy.Deimos.Data;

namespace GatherBuddy.Deimos.Scheduling;

/// picks which mission to grab next off the board, per config (craft-only, current job, for now).
/// by default prefers a mission that grants research XP the current job still needs.
internal static class MissionSelector
{
    public static unsafe uint PickCraft(AgentWKSMission* agent, uint job, DeimosConfig config)
    {
        StdVector<AgentWKSMission.MissionEntry> board = default;
        if (!agent->GetBasicMissions(&board))
            return 0;

        var needed = Wks.NeededResearchTypes(job);

        uint first     = 0; // first valid candidate (fallback when nothing grants needed research)
        uint best      = 0; // candidate granting the most needed research XP
        var  bestScore = 0;

        foreach (var entry in board)
        {
            var id = entry.MissionUnitId;
            if (!MissionData.TryGet(id, out var info))
                continue;
            if (!info.IsCraftOnly || !info.Jobs.Contains(job))
                continue;
            if (config.SkipExpertCrafts && info.IsExpert)
                continue;
            // empty enabled set = run anything
            if (config.EnabledMissions.Count > 0 && !config.EnabledMissions.Contains(id))
                continue;
            if (config.Mode == DeimosMode.MissionGold && Wks.IsMissionGolded(id))
                continue;

            if (first == 0)
                first = id;

            var score = 0;
            foreach (var (type, amount) in info.RelicXpInfo)
                if (needed.Contains(type))
                    score += amount;

            if (score > bestScore)
            {
                bestScore = score;
                best      = id;
            }
        }

        return best != 0 ? best : first;
    }
}
