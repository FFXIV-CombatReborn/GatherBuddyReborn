using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GatherBuddy.Deimos.Crafting;
using GatherBuddy.Deimos.Data;

namespace GatherBuddy.Deimos.Scheduling;

/// drives the mission loop: grab -> execute -> (craft) -> turn in -> repeat, plus abandon.
/// gather/fish/dual missions are routed to a stop until phases 3/7 land.
internal sealed class Scheduler
{
    private readonly CosmicCraftRunner _runner = new();
    private readonly DeimosConfig      _config;

    public DeimosState State { get; private set; } = DeimosState.Idle;

    public Scheduler(DeimosConfig config) => _config = config;

    public void Start() => State = DeimosState.Start;

    public void Stop()
    {
        State = DeimosState.Idle;
        _runner.Stop();
    }

    public void Tick()
    {
        switch (State)
        {
            case DeimosState.Idle:           break;
            case DeimosState.Start:          State = DeimosState.CheckState; break;
            case DeimosState.CheckState:     CheckState(); break;
            case DeimosState.GrabMission:    GrabMission(); break;
            case DeimosState.ExecuteMission: ExecuteMission(); break;
            case DeimosState.Craft:          if (_runner.Update()) State = DeimosState.TurnIn; break;
            case DeimosState.TurnIn:         TurnIn(); break;
            case DeimosState.Abandon:        Abandon(); break;
        }
    }

    private void CheckState()
        => State = Wks.CurrentMissionId != 0 ? DeimosState.ExecuteMission : DeimosState.GrabMission;

    private void ExecuteMission()
    {
        var id = Wks.CurrentMissionId;
        if (id == 0)
        {
            State = DeimosState.CheckState;
            return;
        }

        if (!MissionData.TryGet(id, out var mission))
        {
            DeimosLog.Warning($"Unknown mission {id}, abandoning.");
            State = DeimosState.Abandon;
            return;
        }

        if (mission.IsCraftOnly)
        {
            _runner.Start(mission);
            State = DeimosState.Craft;
        }
        else
        {
            if (DeimosThrottle.Throttle("unsupported", 5000))
                DeimosLog.Info("Active mission isn't a pure crafting mission - gathering/fishing/dual arrive in later phases. Stopping.");
            Stop();
        }
    }

    private unsafe void GrabMission()
    {
        var job = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
        if (!CosmicJobs.IsCrafter(job))
        {
            if (DeimosThrottle.Throttle("not-crafter", 5000))
                DeimosLog.Info("Be on a crafter to grab a cosmic crafting mission (gathering/fishing come later).");
            return;
        }

        var agent = AgentWKSMission.Instance();
        if (agent == null)
            return;

        if (!agent->AgentInterface.IsAgentActive())
        {
            if (DeimosThrottle.Throttle("open-board", 500))
                agent->AgentInterface.Show();
            return;
        }

        var pick = MissionSelector.PickCraft(agent, job, _config);
        if (pick == 0)
        {
            if (DeimosThrottle.Throttle("none-available", 5000))
                DeimosLog.Info("No enabled crafting mission available for this job right now. Stopping.");
            Stop();
            return;
        }

        if (DeimosThrottle.Throttle("initiate", 1000))
        {
            DeimosLog.Info($"Grabbing mission {pick} ({MissionData.Get(pick)?.Name}).");
            Wks.Initiate((ushort)pick);
        }

        State = DeimosState.CheckState;
    }

    private void TurnIn()
    {
        if (Wks.CurrentMissionId == 0)
        {
            DeimosLog.Info("Mission turned in.");
            State = DeimosState.Start; // loop for the next one
            return;
        }

        if (DeimosThrottle.Throttle("report", 1500))
            Wks.Report();
    }

    private void Abandon()
    {
        if (Wks.CurrentMissionId == 0)
        {
            State = DeimosState.Start;
            return;
        }

        if (DeimosThrottle.Throttle("abandon", 1500))
            Wks.Abandon();
    }
}
