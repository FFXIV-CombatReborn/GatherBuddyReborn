using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using GatherBuddy.Crafting;
using GatherBuddy.Deimos.Travel;
using RepairManager = GatherBuddy.Crafting.RepairManager;

namespace GatherBuddy.Deimos.Scheduling;

/// hub-side upkeep after a mission: stellar return, vendor repair, gamba wheel
internal sealed unsafe class HubRunner
{
    private enum Step
    {
        Return,
        RepairWalk,
        RepairRun,
        GambaWalk,
        GambaOpen,
        GambaPlay,
        Done,
    }

    private const int   SpinCost     = 1000;
    private const float InteractDist = 4.0f;
    private const long  GambaStuckMs = 15000;

    private static readonly Dictionary<uint, int> WheelWeights = new()
    {
        // mounts
        [44505] = 200, [47973] = 200, [50441] = 200, [52267] = 200,
        // outfits
        [47937] = 50, [47095] = 50, [50828] = 50, [52605] = 25,
        // emotes + minions
        [44509] = 25, [46795] = 25, [47966] = 25, [46782] = 25, [50323] = 25, [52275] = 25,
        // accessories
        [48154] = 5, [48160] = 5, [46840] = 5, [50458] = 5, [50455] = 5, [52449] = 5,
    };

    private Step _step = Step.Done;
    private int  _repairSub;
    private bool _doRepair;
    private bool _doGamba;
    private long _gambaProgress;

    public bool Active => _step != Step.Done;

    public void Begin(DeimosConfig config)
    {
        _doRepair  = config.AutoRepair && config.RepairAtVendor && RepairManager.NeedsRepair(config.RepairThreshold);
        _doGamba   = config.AutoGamba && LunarCredits() >= SpinCost + config.GambaKeepLunarCredits;
        _repairSub = 0;
        _step      = _doRepair || _doGamba ? Step.Return : Step.Done;

        if (_step != Step.Done)
        {
            CraftingTasks.ResetRepairState();
            DeimosLog.Info($"Hub trip: repair={_doRepair}, gamba={_doGamba}.");
        }
    }

    public void Cancel() => _step = Step.Done;

    /// tick each frame; true when the hub trip is finished
    public bool Update(DeimosConfig config)
    {
        switch (_step)
        {
            case Step.Return:     ReturnToHub(); return false;
            case Step.RepairWalk: WalkTo(HubNpcs.Kind.Repair, Step.RepairRun, AfterRepair()); return false;
            case Step.RepairRun:  RepairRun(); return false;
            case Step.GambaWalk:  WalkTo(HubNpcs.Kind.Gamba, Step.GambaOpen, Step.Done); return false;
            case Step.GambaOpen:  GambaOpen(); return false;
            case Step.GambaPlay:  GambaPlay(config); return false;
            default:              return true;
        }
    }

    private Step AfterRepair() => _doGamba ? Step.GambaWalk : Step.Done;

    private void ReturnToHub()
    {
        if (Dalamud.Conditions[ConditionFlag.BetweenAreas]
         || Dalamud.Conditions[ConditionFlag.BetweenAreas51]
         || Dalamud.Conditions[ConditionFlag.Casting])
            return;

        if (Navigator.NearHub(50f))
        {
            _step = _doRepair ? Step.RepairWalk : Step.GambaWalk;
            return;
        }

        if (DeimosThrottle.Throttle("hub-return", 2500) && !Navigator.CastStellarReturn())
        {
            // return on cooldown - leg it
            if (Navigator.Available && !Navigator.Moving && CosmicZone.HubCenter(CosmicZone.Current) is { } hub)
                Navigator.MoveTo(hub);
        }
    }

    private void WalkTo(HubNpcs.Kind kind, Step next, Step skip)
    {
        if (HubNpcs.Get(CosmicZone.Current, kind) is not { } npc)
        {
            DeimosLog.Warning($"No {kind} npc recorded for this moon, skipping.");
            _step = skip;
            return;
        }

        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return;

        if (Vector3.Distance(player.Position, npc.Position) > InteractDist)
        {
            if (!Navigator.Available)
            {
                DeimosLog.Warning("vnavmesh unavailable, skipping hub stop.");
                _step = skip;
                return;
            }

            if (Navigator.Moving)
                Navigator.EnsureSprint();
            else if (DeimosThrottle.Throttle("hub-walk", 1000))
                Navigator.MoveTo(npc.Position);
            return;
        }

        Navigator.Stop();
        _step = next;
    }

    private void RepairRun()
    {
        switch (_repairSub)
        {
            case 0:
                switch (CraftingTasks.TaskInteractWithRepairNPC())
                {
                    case CraftingTasks.TaskResult.Done:  _repairSub = 1; break;
                    case CraftingTasks.TaskResult.Abort: _step = AfterRepair(); break;
                }
                break;
            case 1:
                switch (CraftingTasks.TaskSelectRepairFromMenu())
                {
                    case CraftingTasks.TaskResult.Done:  _repairSub = 2; break;
                    case CraftingTasks.TaskResult.Abort: _step = AfterRepair(); break;
                }
                break;
            case 2:
                if (CraftingTasks.TaskExecuteRepair(isSelfRepair: false) != CraftingTasks.TaskResult.Retry)
                    _repairSub = 3;
                break;
            default:
                if (CraftingTasks.TaskCloseRepairWindow() != CraftingTasks.TaskResult.Retry)
                {
                    DeimosLog.Info("Vendor repair done.");
                    _step = AfterRepair();
                }
                break;
        }
    }

    private void GambaOpen()
    {
        if (AddonUi.TryGetVisible("WKSLottery", out _))
        {
            _step = Step.GambaPlay;
            Poke();
            return;
        }

        if (AddonUi.TryGetVisible("SelectIconString", out var icons))
        {
            if (DeimosThrottle.Throttle("gamba-pick", 600))
                Callback.Fire(icons, true, 0);
            return;
        }

        if (AddonUi.TryGetVisible("SelectString", out var select))
        {
            if (DeimosThrottle.Throttle("gamba-pick", 600))
                Callback.Fire(select, true, 0);
            return;
        }

        if (AddonUi.TryGetVisible("Talk", out var talk))
        {
            if (DeimosThrottle.Throttle("gamba-talk", 300))
                AddonUi.ClickTalk(talk);
            return;
        }

        // nothing open yet - poke the npc
        if (HubNpcs.Get(CosmicZone.Current, HubNpcs.Kind.Gamba) is not { } npc)
        {
            _step = Step.Done;
            return;
        }

        var obj = NearestById(npc.NpcId);
        if (obj == null)
        {
            _step = Step.Done;
            return;
        }

        if (DeimosThrottle.Throttle("gamba-interact", 1000))
        {
            Dalamud.Targets.Target = obj;
            TargetSystem.Instance()->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address);
        }
    }

    private void GambaPlay(DeimosConfig config)
    {
        if (!AddonUi.TryGetVisible("WKSLottery", out var lottery))
        {
            // wheel closed - click through any trailing dialog and wrap up
            if (AddonUi.TryGetVisible("Talk", out var talk))
            {
                if (DeimosThrottle.Throttle("gamba-talk", 300))
                    AddonUi.ClickTalk(talk);
                return;
            }

            DeimosLog.Info("Gamba done.");
            _step = Step.Done;
            return;
        }

        // watchdog: bail if nothing has progressed in a while
        if (Environment.TickCount64 - _gambaProgress > GambaStuckMs)
        {
            DeimosLog.Warning("Gamba looks stuck, closing the wheel.");
            lottery->Close(true);
            Poke();
            return;
        }

        var affordable = LunarCredits() >= SpinCost + config.GambaKeepLunarCredits;

        if (AddonUi.TryGetVisible("SelectYesno", out var yesno))
        {
            if (DeimosThrottle.Throttle("gamba-yesno", 600))
            {
                Callback.Fire(yesno, true, affordable ? 0 : 1);
                Poke();
            }
            return;
        }

        if (!affordable)
        {
            // out of credits: the wheel doesn't react to callback -1, close it directly
            if (DeimosThrottle.Throttle("gamba-close", 1000))
            {
                DeimosLog.Info("Out of gamba credits, closing the wheel.");
                lottery->Close(true);
                Poke();
            }
            return;
        }

        var spin = lottery->GetComponentButtonById(64);
        if (spin != null && spin->IsEnabled)
        {
            if (DeimosThrottle.Throttle("gamba-spin", 800))
            {
                ClickButton(lottery, spin);
                Poke();
            }
            return;
        }

        var left  = lottery->GetComponentButtonById(29);
        var right = lottery->GetComponentButtonById(39);
        if (left != null && right != null && (left->IsEnabled || right->IsEnabled))
        {
            if (!DeimosThrottle.Throttle("gamba-wheel", 800))
                return;

            var leftItems  = WheelItems(lottery, 89);
            var rightItems = WheelItems(lottery, 138);
            var pickLeft   = PickLeftWheel(leftItems, rightItems);

            // selected = checked+enabled+selected, other = just enabled
            left->Flags  = pickLeft ? 327936u : 65792u;
            right->Flags = pickLeft ? 65792u : 327936u;
            DeimosLog.Debug($"Gamba wheel: {(pickLeft ? "left" : "right")}.");
            Poke();
        }
    }

    private void Poke() => _gambaProgress = Environment.TickCount64;

    // empty wheel wins (all-credit wheel), else higher default weight
    private static bool PickLeftWheel(List<uint> left, List<uint> right)
    {
        if (left.Count == 0)
            return true;
        if (right.Count == 0)
            return false;

        int Score(List<uint> ids)
        {
            var total = 0;
            foreach (var id in ids)
                total += WheelWeights.GetValueOrDefault(id);
            return total;
        }

        var l = Score(left);
        var r = Score(right);
        return l != r ? l > r : Random.Shared.Next(2) == 0;
    }

    private static List<uint> WheelItems(AtkUnitBase* lottery, int firstIndex)
    {
        var ids = new List<uint>();
        for (var i = 0; i < 7; i++)
        {
            var index = firstIndex + i * 7;
            if (lottery->AtkValuesCount <= index)
                break;
            var id = lottery->AtkValues[index].UInt;
            if (id != 0)
                ids.Add(id);
        }
        return ids;
    }


    private static IGameObject? NearestById(uint baseId)
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return null;

        IGameObject? best = null;
        var bestDist = float.MaxValue;
        foreach (var obj in Dalamud.Objects)
        {
            if (obj.BaseId != baseId)
                continue;
            var dist = Vector3.Distance(obj.Position, player.Position);
            if (dist < bestDist)
            {
                bestDist = dist;
                best     = obj;
            }
        }
        return best;
    }

    private static void ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        var node = button->AtkComponentBase.OwnerNode;
        if (node == null)
            return;
        var evt = (AtkEvent*)node->AtkResNode.AtkEventManager.Event;
        if (evt == null)
            return;
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt);
    }


    private static int LunarCredits()
    {
        var item = CosmicZone.LunarCreditItem(CosmicZone.Current);
        if (item == 0)
            return 0;
        var inv = InventoryManager.Instance();
        return inv == null ? 0 : inv->GetInventoryItemCount(item, false, false, false);
    }
}
