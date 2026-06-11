using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GatherBuddy.Automation;
using GatherBuddy.Deimos.Crafting;
using GatherBuddy.Deimos.Data;
using GatherBuddy.Deimos.Travel;

namespace GatherBuddy.Deimos.Scheduling;

/// drives the mission loop: grab -> execute -> (craft) -> turn in -> repeat, plus abandon.
/// gather/fish/dual missions are routed to a stop for now
internal sealed class Scheduler
{
    private readonly CosmicCraftRunner _runner      = new();
    private readonly MaintenanceRunner _maintenance = new();
    private readonly HubRunner         _hub         = new();
    private readonly DeimosConfig      _config;

    private int _rerollsUsed;

    // board scan across job tabs (no gearset swaps needed to look)
    private int  _scanIndex = -1;
    private long _tabSetAt;
    private uint _bestJob;
    private uint _bestPick;
    private int  _bestScore;
    private readonly HashSet<uint> _noGearset = new();

    public int MissionsDone { get; private set; }

    public DeimosState State { get; private set; } = DeimosState.Idle;

    public Scheduler(DeimosConfig config) => _config = config;

    public void Start() => State = DeimosState.Start;

    public void Stop()
    {
        State        = DeimosState.Idle;
        _rerollsUsed = 0;
        MissionsDone = 0;
        _noGearset.Clear();
        ResetScan();
        _runner.Stop();
        _hub.Cancel();
        Navigator.Stop();
    }

    private void ResetScan()
    {
        _scanIndex = -1;
        _bestJob   = 0;
        _bestPick  = 0;
        _bestScore = -1;
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
            case DeimosState.Maintenance:    Maintenance(); break;
            case DeimosState.Abandon:        Abandon(); break;
        }
    }

    private void CheckState()
    {
        ResetScan();
        State = Wks.CurrentMissionId != 0 ? DeimosState.ExecuteMission : DeimosState.GrabMission;
    }

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
                DeimosLog.Info("Active mission isn't a pure crafting mission - gathering/fishing/dual not implemented. Stopping.");
            Stop();
        }
    }

    private unsafe void GrabMission()
    {
        var current = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;

        var jobs = _config.JobPriority.Where(CosmicJobs.IsCrafter).Where(j => !_noGearset.Contains(j)).Distinct().ToList();
        if (jobs.Count == 0)
        {
            if (!CosmicJobs.IsCrafter(current))
            {
                if (DeimosThrottle.Throttle("not-crafter", 5000))
                    DeimosLog.Info("Be on a crafter (or set a job priority list) to grab a crafting mission.");
                return;
            }
            jobs.Add(current);
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

        // scan every job's tab first - no gearset swaps just to look
        if (_scanIndex < jobs.Count)
        {
            if (_scanIndex < 0)
            {
                _scanIndex = 0;
                Wks.SetBoardJobTab(jobs[0]);
                _tabSetAt = Environment.TickCount64;
                return;
            }

            if (Environment.TickCount64 - _tabSetAt < 300)
                return; // let the board refresh onto the tab

            var job  = jobs[_scanIndex];
            var pick = MissionSelector.PickCraftScored(agent, job, _config, out var score);
            if (pick != 0 && score > _bestScore)
            {
                _bestScore = score;
                _bestJob   = job;
                _bestPick  = pick;
            }

            _scanIndex++;
            if (_scanIndex < jobs.Count)
            {
                Wks.SetBoardJobTab(jobs[_scanIndex]);
                _tabSetAt = Environment.TickCount64;
                return;
            }

            // scan complete - point the board back at the winner
            if (_bestPick != 0)
            {
                Wks.SetBoardJobTab(_bestJob);
                _tabSetAt = Environment.TickCount64;
            }
            return;
        }

        if (_bestPick == 0)
        {
            // every job came up empty - reroll the board or stop
            if (_config.AutoReroll && _rerollsUsed < _config.MaxRerolls)
            {
                var fodderJob = CosmicJobs.IsCrafter(current) ? current : jobs[0];
                Wks.SetBoardJobTab(fodderJob);
                var fodder = MissionSelector.AnyCraft(agent, fodderJob);
                if (fodder != 0 && current == fodderJob)
                {
                    if (DeimosThrottle.Throttle("reroll", 1000))
                    {
                        _rerollsUsed++;
                        DeimosLog.Info($"Rerolling the board ({_rerollsUsed}/{_config.MaxRerolls}): grab+abandon {fodder}.");
                        Wks.Initiate((ushort)fodder);
                        State = DeimosState.Abandon;
                    }
                    return;
                }
            }

            if (DeimosThrottle.Throttle("none-available", 5000))
                DeimosLog.Info("No enabled crafting mission available on any configured job. Stopping.");
            Stop();
            return;
        }

        // swap to the winning job if needed (board reopens on its tab)
        if (current != _bestJob)
        {
            if (agent->AgentInterface.IsAgentActive())
            {
                if (DeimosThrottle.Throttle("hide-board", 500))
                    agent->AgentInterface.Hide();
                return;
            }

            if (DeimosThrottle.Throttle("job-swap", 2000) && !JobSwap.TryEquip(_bestJob))
            {
                DeimosLog.Warning($"No gearset found for {CosmicJobs.Name(_bestJob)}, skipping that job.");
                _noGearset.Add(_bestJob);
                ResetScan();
            }
            return;
        }

        if (Environment.TickCount64 - _tabSetAt < 300)
            return;

        // re-verify on the live board (it may have rolled since the scan)
        var verify = MissionSelector.PickCraft(agent, _bestJob, _config);
        if (verify == 0)
        {
            ResetScan();
            return;
        }

        if (DeimosThrottle.Throttle("initiate", 1000))
        {
            DeimosLog.Info($"Grabbing mission {verify} ({MissionData.Get(verify)?.Name}) on {CosmicJobs.Name(_bestJob)}.");
            Wks.Initiate((ushort)verify);
            _rerollsUsed = 0;
            ResetScan();
            State = DeimosState.CheckState;
        }
    }

    private void TurnIn()
    {
        var id = Wks.CurrentMissionId;
        if (id == 0)
        {
            Navigator.Stop();
            MissionsDone++;
            DeimosLog.Info($"Mission turned in ({MissionsDone} this run).");
            _maintenance.Begin();
            _hub.Begin(_config);
            State = DeimosState.Maintenance;
            return;
        }

        // criticals deliver at the red-alert collection point instead of reporting
        if (MissionData.TryGet(id, out var mission) && mission.IsCritical)
        {
            CriticalTurnIn(mission);
            return;
        }

        if (DeimosThrottle.Throttle("report", 1500))
            Wks.Report();
    }

    private unsafe void CriticalTurnIn(CosmicInfo mission)
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return;

        var point = Dalamud.Objects
            .Where(o => o.BaseId is CosmicZone.CollectionPointA or CosmicZone.CollectionPointB)
            .OrderBy(o => Vector3.Distance(o.Position, player.Position))
            .FirstOrDefault();

        // collection point not in object range yet - get to the site
        if (point == null)
        {
            if (!Navigator.Available)
            {
                if (DeimosThrottle.Throttle("no-navmesh", 5000))
                    DeimosLog.Warning("Critical turn-in needs vnavmesh installed. Stopping.");
                Stop();
                return;
            }

            var hasSite = CriticalSites.TryGet(mission.MarkerId, out var site);

            // close enough to walk straight in
            if (hasSite && Vector3.Distance(player.Position, site.Spot) < 75f)
            {
                if (Navigator.Moving)
                {
                    Navigator.EnsureSprint();
                    return;
                }
                if (MountingUp())
                    return;
                if (DeimosThrottle.Throttle("crit-travel", 2000))
                    Navigator.MoveTo(site.Spot);
                return;
            }

            // far away with a known site: the hub red-alert npc teleports us there
            if (hasSite)
            {
                CriticalTeleport(site);
                return;
            }

            // unknown site - fall back to walking at the map marker
            if (Navigator.Moving)
            {
                Navigator.EnsureSprint();
                return;
            }

            if (MountingUp())
                return;

            if (DeimosThrottle.Throttle("crit-travel", 2000))
            {
                var ground = Navigator.Ground(mission.MapPosition.X, mission.MapPosition.Y);
                if (ground is { } destination)
                {
                    DeimosLog.Info($"Heading to the critical site at ~({mission.MapPosition.X:F0}, {mission.MapPosition.Y:F0}).");
                    Navigator.MoveTo(destination);
                }
                else
                {
                    DeimosLog.Warning("Couldn't resolve ground at the critical site. Stopping.");
                    Stop();
                }
            }
            return;
        }

        // approach the point
        var distance = Vector3.Distance(player.Position, point.Position);
        if (distance > 4.5f)
        {
            if (!Navigator.Available)
            {
                if (DeimosThrottle.Throttle("no-navmesh", 5000))
                    DeimosLog.Warning("Critical turn-in needs vnavmesh installed. Stopping.");
                Stop();
                return;
            }

            if (Navigator.Moving)
            {
                Navigator.EnsureSprint();
                return;
            }

            // mount for the long hops, walk the short ones
            if (distance > 30f && MountingUp())
                return;

            if (DeimosThrottle.Throttle("crit-approach", 1000))
                Navigator.MoveTo(point.Position);
            return;
        }

        Navigator.Stop();

        // can't interact from mount-back
        if (Navigator.Mounted)
        {
            if (DeimosThrottle.Throttle("dismount", 800))
                Navigator.Dismount();
            return;
        }

        // deliver: interact until the mission clears (dialog in progress = wait)
        if (Dalamud.Conditions[ConditionFlag.OccupiedInQuestEvent] || Dalamud.Conditions[ConditionFlag.OccupiedInEvent])
            return;

        if (DeimosThrottle.Throttle("crit-interact", 800))
        {
            Dalamud.Targets.Target = point;
            TargetSystem.Instance()->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)point.Address);
        }
    }

    private void Maintenance()
    {
        if (!_maintenance.Update(_config))
            return;

        if (_hub.Active && !_hub.Update(_config))
            return;

        if (ShouldStop())
            Stop();
        else
            State = DeimosState.Start; // loop for the next one
    }

    private bool ShouldStop()
    {
        if (_config.StopAfterMissions > 0 && MissionsDone >= _config.StopAfterMissions)
        {
            DeimosLog.Info($"Stop condition met: {MissionsDone} missions done.");
            return true;
        }

        if (_config.StopAtLunarCredits > 0)
        {
            var item = CosmicZone.LunarCreditItem(CosmicZone.Current);
            if (item != 0 && ItemCount(item) >= _config.StopAtLunarCredits)
            {
                DeimosLog.Info($"Stop condition met: lunar credits at {ItemCount(item)}.");
                return true;
            }
        }

        if (_config.StopAtCosmoCredits > 0 && ItemCount(CosmicZone.CosmoCreditItem) >= _config.StopAtCosmoCredits)
        {
            DeimosLog.Info($"Stop condition met: cosmo credits at {ItemCount(CosmicZone.CosmoCreditItem)}.");
            return true;
        }

        return false;
    }

    private static unsafe int ItemCount(uint itemId)
    {
        var inv = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        return inv == null ? 0 : inv->GetInventoryItemCount(itemId, false, false, false);
    }

    // ride the hub red-alert npc's teleport to the active critical site
    private unsafe void CriticalTeleport(CriticalSites.Site site)
    {
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas]
         || Dalamud.Conditions[ConditionFlag.BetweenAreas51]
         || Dalamud.Conditions[ConditionFlag.Casting])
            return;

        // dialog chain: talk -> site list -> confirm
        if (AddonUi.TryGetVisible("SelectString", out var select))
        {
            if (DeimosThrottle.Throttle("crit-npc", 600))
            {
                DeimosLog.Info($"Taking red-alert teleport option {site.NpcChoice}.");
                Callback.Fire(select, true, site.NpcChoice);
            }
            return;
        }

        if (AddonUi.TryGetVisible("SelectYesno", out var yesno))
        {
            if (DeimosThrottle.Throttle("crit-npc", 600))
                Callback.Fire(yesno, true, 0);
            return;
        }

        if (AddonUi.TryGetVisible("Talk", out var talk))
        {
            if (DeimosThrottle.Throttle("crit-talk", 300))
                AddonUi.ClickTalk(talk);
            return;
        }

        if (HubNpcs.Get(CosmicZone.Current, HubNpcs.Kind.RedAlert) is not { } npc)
        {
            DeimosLog.Warning("No red-alert npc recorded for this moon. Stopping.");
            Stop();
            return;
        }

        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return;

        var obj = Dalamud.Objects
            .Where(o => o.BaseId == npc.NpcId)
            .OrderBy(o => Vector3.Distance(o.Position, player.Position))
            .FirstOrDefault();

        if (obj != null && Vector3.Distance(player.Position, obj.Position) < 5f)
        {
            Navigator.Stop();
            if (Navigator.Mounted)
            {
                if (DeimosThrottle.Throttle("dismount", 800))
                    Navigator.Dismount();
                return;
            }

            if (DeimosThrottle.Throttle("crit-interact", 1000))
            {
                Dalamud.Targets.Target = obj;
                TargetSystem.Instance()->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address);
            }
            return;
        }

        // not at the hub npc yet: stellar return, then walk over
        if (!Navigator.NearHub())
        {
            if (DeimosThrottle.Throttle("crit-return", 2500) && !Navigator.CastStellarReturn()
             && Navigator.Available && !Navigator.Moving && CosmicZone.HubCenter(CosmicZone.Current) is { } hub)
                Navigator.MoveTo(hub);
            return;
        }

        if (Navigator.Moving)
        {
            Navigator.EnsureSprint();
            return;
        }

        if (DeimosThrottle.Throttle("crit-npc-walk", 1000))
            Navigator.MoveTo(npc.Position);
    }

    // mount up before a long leg; true = busy mounting, false = mounted or roulette unavailable (walk)
    private static bool MountingUp()
    {
        if (Navigator.Mounted)
            return false;
        if (Dalamud.Conditions[ConditionFlag.Casting])
            return true; // roulette cast in flight
        return DeimosThrottle.Throttle("mount-up", 1500) && Navigator.CastMountRoulette();
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
