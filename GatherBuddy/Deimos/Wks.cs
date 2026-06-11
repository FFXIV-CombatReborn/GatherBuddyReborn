using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.WKS;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.STD;

namespace GatherBuddy.Deimos;

/// safe accessor over the game's WKSManager (cosmic state)
internal static unsafe class Wks
{
    public static WKSManager* Manager => WKSManager.Instance();

    public static bool Available => Manager != null;

    /// active mission row id, 0 if none
    public static uint CurrentMissionId
    {
        get
        {
            var m = Manager;
            // State is internal to plugins; use the obsolete top-level field
#pragma warning disable CS0618
            return m != null ? m->CurrentMissionUnitRowId : 0u;
#pragma warning restore CS0618
        }
    }

    public static bool IsMissionCompleted(uint missionUnitId)
    {
        var m = Manager;
        return m != null && m->IsMissionCompleted(missionUnitId);
    }

    public static bool IsMissionGolded(uint missionUnitId)
    {
        var m = Manager;
        return m != null && m->IsMissionGolded(missionUnitId);
    }

    /// live score of the active mission
    public static uint CurrentScore
    {
        get
        {
            var m = Manager;
            // State is internal to plugins; the obsolete top-level field is what we can read
#pragma warning disable CS0618
            return m != null ? m->CurrentScore : 0u;
#pragma warning restore CS0618
        }
    }

    /// true once the active mission has reached gold (its max rank)
    public static bool IsCurrentMissionGold
    {
        get
        {
            var m = Manager;
            if (m == null)
                return false;
#pragma warning disable CS0618
            return m->CurrentRank == WKSManager.MissionRank.Gold;
#pragma warning restore CS0618
        }
    }

    private static WKSMissionModule* MissionModule
    {
        get
        {
            var m = Manager;
            return m != null ? m->MissionModule : null;
        }
    }

    public static void Initiate(ushort missionUnitId)
    {
        var mm = MissionModule;
        if (mm != null)
            mm->InitiateMission(missionUnitId);
    }

    public static void Report()
    {
        var mm = MissionModule;
        if (mm != null)
            mm->ReportMission();
    }

    public static void Abandon()
    {
        var mm = MissionModule;
        if (mm != null)
            mm->AbandonMission();
    }

    // this sig can be dropped once Structs ref is udpated
    private delegate bool GetMissionsDelegate(AgentWKSMission* agent, StdVector<AgentWKSMission.MissionEntry>* list);
    private static GetMissionsDelegate? _getMasterMissions;
    private static bool _masterSigTried;

    public static bool TryGetMasterMissions(AgentWKSMission* agent, StdVector<AgentWKSMission.MissionEntry>* list)
    {
        if (!_masterSigTried)
        {
            _masterSigTried = true;
            try
            {
                var ptr = Dalamud.SigScanner.ScanText("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 83 EC ?? 4C 8B F2 48 8B D9 E8 ?? ?? ?? ?? 48 8B 4B");
                _getMasterMissions = Marshal.GetDelegateForFunctionPointer<GetMissionsDelegate>(ptr);
            }
            catch (Exception ex)
            {
                DeimosLog.Warning($"Mastery mission list unavailable (sig not found): {ex.Message}");
            }
        }

        return _getMasterMissions != null && agent != null && _getMasterMissions(agent, list);
    }

    /// point the mission board at a job's tab without swapping gearsets (board lists follow the tab)
    public static bool SetBoardJobTab(uint classJobId, byte categoryTab = 0)
    {
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentWKSMission.Instance();
        if (agent == null || agent->Data == null)
            return false;

        byte jobIndex = 0;
        var found = false;
        for (byte i = 0; i <= 11; i++)
        {
            if (agent->JobIndexToClassJobId(i) != classJobId)
                continue;
            jobIndex = i;
            found = true;
            break;
        }
        if (!found)
            return false;

        agent->SelectedTab = categoryTab;
        agent->Data->SelectedJobIndex = jobIndex;
        agent->Data->UpdateFlags = 1;
        // HasSavedTab is private; clear it so the game doesn't revert our tab next tick
        *((byte*)agent + 0x35) = 0;
        return true;
    }

    /// research XP types (1-6) the given job still needs (current analysis below what's needed)
    public static HashSet<int> NeededResearchTypes(uint job)
    {
        var needed = new HashSet<int>();
        var m = Manager;
        var rm = m != null ? m->ResearchModule : null;
        if (rm == null || !rm->IsLoaded)
            return needed;

        var toolClass = (byte)(job - 7); // CRP(8) -> 1 ... FSH(18) -> 11
        for (byte type = 1; type <= 6; type++)
        {
            if (!rm->IsTypeAvailable(toolClass, type))
                break;
            if (rm->GetCurrentAnalysis(toolClass, type) < rm->GetNeededAnalysis(toolClass, type))
                needed.Add(type);
        }

        return needed;
    }
}
