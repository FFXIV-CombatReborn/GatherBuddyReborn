using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Crafting;
using GatherBuddy.Deimos.Data;

namespace GatherBuddy.Deimos.Crafting;

/// drives a mission's crafts to completion: pre-crafts first, then mains, one synth at a time.
/// stops and lets the mission be turned in once it's maxed (gold) or out of materials.
internal sealed class CosmicCraftRunner
{
    // how long to let a synth window open after kicking it before assuming we can't craft
    private const long GraceMs = 8000;

    private CosmicInfo? _mission;
    private bool _waiting;        // a synth was kicked; waiting for it to run/finish
    private bool _synthSeen;      // the synth actually engaged (guards transient idle/log states)
    private bool _outOfMaterials; // a craft was issued but never started -> nothing left to make
    private long _issuedAt;

    public bool Active { get; private set; }

    public void Start(CosmicInfo mission)
    {
        _mission        = mission;
        Active          = true;
        _waiting        = false;
        _synthSeen      = false;
        _outOfMaterials = false;
    }

    public void Stop()
    {
        Active          = false;
        _mission        = null;
        _waiting        = false;
        _synthSeen      = false;
        _outOfMaterials = false;
    }

    /// tick this each frame; returns true once the mission's crafting is finished
    public bool Update()
    {
        if (!Active || _mission == null)
            return true;

        var state = CraftingGameInterop.CurrentState;

        if (state == CraftingGameInterop.CraftState.PreparingCraft)
            return false;

        if (state is CraftingGameInterop.CraftState.WaitStart or CraftingGameInterop.CraftState.InProgress
                  or CraftingGameInterop.CraftState.WaitAction or CraftingGameInterop.CraftState.WaitFinish
                  or CraftingGameInterop.CraftState.QuickSynthesis)
        {
            _synthSeen = true;
            return false;
        }

        // idle: settle the previous synth, or detect a craft that never started
        if (_waiting)
        {
            if (!_synthSeen)
            {
                if (Environment.TickCount64 - _issuedAt < GraceMs)
                    return false; // window still opening
                // never engaged -> out of materials / blocked; finish with what we have
                DeimosLog.Info("Craft didn't start (likely out of materials) - finishing the mission.");
                _outOfMaterials = true;
            }

            _waiting   = false;
            _synthSeen = false;
        }

        var done = _outOfMaterials || Wks.IsCurrentMissionGold;
        var next = done ? null : NextCraft(_mission);
        if (next == null)
        {
            // nothing left to craft - close the log so the mission can be reported
            if (state != CraftingGameInterop.CraftState.IdleNormal)
            {
                if (DeimosThrottle.Throttle("exit-craft", 500))
                    CraftingTasks.TaskExitCraft();
                return false;
            }

            Stop();
            return true;
        }

        CosmicCraft.TryStart(next.Value.Key, _mission);
        _waiting   = true;
        _synthSeen = false;
        _issuedAt  = Environment.TickCount64;
        return false;
    }

    private static (ushort Key, CraftingInfo Info)? NextCraft(CosmicInfo mission)
    {
        // main crafts drive the loop; a pre-craft is only made when an unfinished main still needs it
        foreach (var (mainKey, main) in mission.Crafts_Main)
        {
            if (InventoryCount(main.ItemId) >= main.RequiredAmount)
                continue;

            var blockedByPre = false;
            foreach (var (preKey, pre) in mission.Crafts_Pre)
            {
                if (!main.RequiredItems.TryGetValue(pre.ItemId, out var needPerCraft))
                    continue;
                if (InventoryCount(pre.ItemId) >= needPerCraft)
                    continue;
                if (CanCraft(pre))
                    return (preKey, pre);
                blockedByPre = true; // out of base materials for the intermediate
            }

            if (blockedByPre)
                continue;

            if (CanCraft(main))
                return (mainKey, main);
        }

        return null;
    }

    // do we have at least one of every input this craft consumes?
    private static bool CanCraft(CraftingInfo info)
        => info.RequiredItems.Keys.All(id => InventoryCount(id) > 0);

    // crafted items can be HQ, so count both qualities or targets are never met
    private static unsafe int InventoryCount(uint itemId)
    {
        var inv = InventoryManager.Instance();
        if (inv == null)
            return 0;
        return inv->GetInventoryItemCount(itemId, false, false, false)
             + inv->GetInventoryItemCount(itemId, true, false, false);
    }
}
