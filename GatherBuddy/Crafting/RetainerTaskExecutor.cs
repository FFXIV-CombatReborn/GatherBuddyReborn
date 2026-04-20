using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

internal unsafe class RetainerTaskExecutor
{
    private enum Phase
    {
        InteractBell,
        WaitOccupied,
        WaitRetainerList,
        SelectRetainer,
        WaitRetainerMenu,
        SelectEntrustWithdraw,
        WaitInventory,
        WithdrawNextItem,
        FindItemSlot,
        OpenContextMenu,
        WaitContextMenu,
        SelectRetrieveOption,
        WaitNumericInput,
        InputNumericValue,
        WaitWithdrawComplete,
        CloseRetainerInventory,
        WaitInventoryClosed,
        SelectQuit,
        WaitQuit,
        CloseRetainerList,
        WaitListClosed,
        Complete,
        Aborted
    }

    private record struct RetainerEntry(uint SortedIndex, ulong RetainerId);

    private record struct WithdrawTarget(uint ItemId, int AmountHQ, int AmountNQ)
    {
        public int RemainingHQ = AmountHQ;
        public int RemainingNQ = AmountNQ;
        public int Total => RemainingHQ + RemainingNQ;
    }

    private Phase _phase = Phase.InteractBell;
    private DateTime _nextRetry = DateTime.MinValue;

    private List<RetainerEntry> _retainersToVisit = new();
    private int _retainerVisitIndex = 0;

    private List<WithdrawTarget> _currentRetainerItems = new();
    private int _currentItemIndex = 0;
    private bool _lookingForHQ = false;
    private int _foundSlotQty = 0;

    private int _addonRetryCount = 0;
    private const int MaxAddonRetries = 40;
    private readonly Dictionary<uint, int> _materials;
    private readonly Dictionary<uint, IngredientQualityDemand> _qualityTargets;
    private readonly HashSet<uint> _precraftItemIds;
    private bool _withdrawalPlanBuilt;

    public bool IsComplete => _phase == Phase.Complete;
    public bool IsAborted  => _phase == Phase.Aborted;

    public RetainerTaskExecutor(
        Dictionary<uint, int> materials,
        Dictionary<uint, IngredientQualityDemand> qualityTargets,
        HashSet<uint>? precraftItemIds = null)
    {
        _materials = materials;
        _qualityTargets = qualityTargets;
        _precraftItemIds = precraftItemIds ?? [];
    }

    private bool BuildWithdrawalPlan()
    {
        _retainersToVisit.Clear();
        var perRetainerPlan = new Dictionary<ulong, Dictionary<uint, (int NeedHQ, int NeedNQ)>>();
        var retainerIds = RetainerItemQuery.GetOwnedRetainerIds();
        if (retainerIds.Count == 0)
            retainerIds = GetRetainerIdsFromManager();

        if (retainerIds.Count == 0)
        {
            GatherBuddy.Log.Debug("[RetainerTaskExecutor] No retainer ids available to build withdrawal plan yet");
            return false;
        }

        foreach (var (itemId, totalNeeded) in _materials)
        {
            var demand = _qualityTargets.TryGetValue(itemId, out var qualityDemand)
                ? qualityDemand
                : IngredientQualityDemand.FromPreferHQ(totalNeeded);

            IngredientQualityDemand remainingDemand;
            if (_precraftItemIds.Contains(itemId))
            {
                GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Precraft item {itemId}: pulling exact retainer amount {demand.Total} (inventory already accounted for in plan)");
                remainingDemand = demand;
            }
            else
            {
                int inBagHQ = 0, inBagNQ = 0;
                var inventoryMgr = InventoryManager.Instance();
                if (inventoryMgr != null)
                {
                    inBagHQ = (int)inventoryMgr->GetInventoryItemCount(itemId, true,  false, false);
                    inBagNQ = (int)inventoryMgr->GetInventoryItemCount(itemId, false, false, false);
                }
                remainingDemand = demand.ConsumeSplit(inBagNQ, inBagHQ, out _, out _);
            }

            if (remainingDemand.Total <= 0)
                continue;

            foreach (var retainerId in retainerIds)
            {

                int retainerHQ = 0, retainerNQ = 0;
                for (uint page = 10000; page <= 10006; page++)
                {
                    var pageHQ = (int)AllaganTools.ItemCountHQ(itemId, retainerId, page);
                    retainerHQ += pageHQ;
                    retainerNQ += (int)AllaganTools.ItemCount(itemId, retainerId, page) - pageHQ;
                }
                var crystalPageHQ = (int)AllaganTools.ItemCountHQ(itemId, retainerId, 12001);
                retainerHQ += crystalPageHQ;
                retainerNQ += (int)AllaganTools.ItemCount(itemId, retainerId, 12001) - crystalPageHQ;
                var updatedDemand = remainingDemand.ConsumeSplit(retainerNQ, retainerHQ, out var toTakeNQ, out var toTakeHQ);

                if (toTakeHQ <= 0 && toTakeNQ <= 0)
                    continue;

                if (!perRetainerPlan.ContainsKey(retainerId))
                    perRetainerPlan[retainerId] = new();

                if (perRetainerPlan[retainerId].TryGetValue(itemId, out var existing))
                    perRetainerPlan[retainerId][itemId] = (existing.NeedHQ + toTakeHQ, existing.NeedNQ + toTakeNQ);
                else
                    perRetainerPlan[retainerId][itemId] = (toTakeHQ, toTakeNQ);

                remainingDemand = updatedDemand;
                if (remainingDemand.Total <= 0)
                    break;
            }
        }

        _perRetainerPlan = perRetainerPlan;
        if (_perRetainerPlan.Count == 0)
        {
            GatherBuddy.Log.Debug("[RetainerTaskExecutor] No retainer items to withdraw, completing immediately");
            return true;
        }

        var retainerMgr = RetainerManager.Instance();
        if (retainerMgr == null)
        {
            GatherBuddy.Log.Debug("[RetainerTaskExecutor] RetainerManager unavailable while mapping withdrawal plan");
            return false;
        }

        for (uint i = 0; i < retainerMgr->GetRetainerCount(); i++)
        {
            var retainer = retainerMgr->GetRetainerBySortedIndex(i);
            if (retainer == null || retainer->RetainerId == 0)
                continue;

            if (!_perRetainerPlan.TryGetValue(retainer->RetainerId, out var items))
                continue;

            if (!items.Values.Any(v => v.NeedHQ > 0 || v.NeedNQ > 0))
                continue;

            _retainersToVisit.Add(new RetainerEntry(i, retainer->RetainerId));
        }

        GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Plan: {_retainersToVisit.Count} retainer(s) to visit");
        foreach (var r in _retainersToVisit)
        {
            var items = _perRetainerPlan[r.RetainerId];
            foreach (var (itemId, amounts) in items)
                GatherBuddy.Log.Debug($"[RetainerTaskExecutor]   Retainer {r.SortedIndex} ({r.RetainerId}): item {itemId} HQ={amounts.NeedHQ} NQ={amounts.NeedNQ}");
        }

        return _retainersToVisit.Count > 0;
    }

    private Dictionary<ulong, Dictionary<uint, (int NeedHQ, int NeedNQ)>> _perRetainerPlan = new();

    public CraftingTasks.TaskResult Tick()
    {
        if (DateTime.Now < _nextRetry)
            return CraftingTasks.TaskResult.Retry;

        return _phase switch
        {
            Phase.InteractBell           => TickInteractBell(),
            Phase.WaitOccupied           => TickWaitOccupied(),
            Phase.WaitRetainerList       => TickWaitRetainerList(),
            Phase.SelectRetainer         => TickSelectRetainer(),
            Phase.WaitRetainerMenu       => TickWaitRetainerMenu(),
            Phase.SelectEntrustWithdraw  => TickSelectEntrustWithdraw(),
            Phase.WaitInventory          => TickWaitInventory(),
            Phase.WithdrawNextItem       => TickWithdrawNextItem(),
            Phase.FindItemSlot           => TickFindItemSlot(),
            Phase.OpenContextMenu        => TickOpenContextMenu(),
            Phase.WaitContextMenu        => TickWaitContextMenu(),
            Phase.SelectRetrieveOption   => TickSelectRetrieveOption(),
            Phase.WaitNumericInput       => TickWaitNumericInput(),
            Phase.InputNumericValue      => TickInputNumericValue(),
            Phase.WaitWithdrawComplete   => TickWaitWithdrawComplete(),
            Phase.CloseRetainerInventory => TickCloseRetainerInventory(),
            Phase.WaitInventoryClosed    => TickWaitInventoryClosed(),
            Phase.SelectQuit             => TickSelectQuit(),
            Phase.WaitQuit               => TickWaitQuit(),
            Phase.CloseRetainerList      => TickCloseRetainerList(),
            Phase.WaitListClosed         => TickWaitListClosed(),
            Phase.Complete               => CraftingTasks.TaskResult.Done,
            Phase.Aborted                => CraftingTasks.TaskResult.Done,
            _                            => CraftingTasks.TaskResult.Done
        };
    }

    private CraftingTasks.TaskResult TickInteractBell()
    {
        if (Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell])
        {
            GatherBuddy.Log.Debug("[RetainerTaskExecutor] Already at bell, waiting for retainer list");
            _phase = Phase.WaitRetainerList;
            return CraftingTasks.TaskResult.Retry;
        }

        var bell = FindNearestBell();
        if (bell == null)
        {
            GatherBuddy.Log.Warning("[RetainerTaskExecutor] No reachable retainer bell found, aborting retainer stage");
            _phase = Phase.Aborted;
            return CraftingTasks.TaskResult.Done;
        }

        TargetSystem.Instance()->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)bell.Address);
        _phase = Phase.WaitOccupied;
        _addonRetryCount = 0;
        Delay(600);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitOccupied()
    {
        if (Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell])
        {
            _phase = Phase.WaitRetainerList;
            _addonRetryCount = 0;
            return CraftingTasks.TaskResult.Retry;
        }

        if (GenericHelpers.TryGetAddonByName<AddonTalk>("Talk", out var talk) && talk->AtkUnitBase.IsVisible)
        {
            GatherBuddy.Log.Debug("[RetainerTaskExecutor] Talk dialog visible, dismissing");
            new AddonMaster.Talk((nint)talk).Click();
            Delay(300);
            return CraftingTasks.TaskResult.Retry;
        }

        _addonRetryCount++;
        if (_addonRetryCount > MaxAddonRetries)
        {
            GatherBuddy.Log.Warning("[RetainerTaskExecutor] Timed out waiting for OccupiedSummoningBell");
            _phase = Phase.Aborted;
            return CraftingTasks.TaskResult.Done;
        }

        Delay(150);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitRetainerList()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && addon->IsVisible)
        {
            if (!_withdrawalPlanBuilt)
            {
                if (!BuildWithdrawalPlan())
                {
                    _addonRetryCount++;
                    if (_addonRetryCount > MaxAddonRetries)
                    {
                        GatherBuddy.Log.Warning("[RetainerTaskExecutor] Timed out building retainer withdrawal plan after opening RetainerList");
                        _phase = Phase.Aborted;
                        return CraftingTasks.TaskResult.Done;
                    }

                    Delay(150);
                    return CraftingTasks.TaskResult.Retry;
                }

                _withdrawalPlanBuilt = true;
            }
            _retainerVisitIndex = 0;
            _addonRetryCount = 0;
            _phase = _retainersToVisit.Count == 0 ? Phase.CloseRetainerList : Phase.SelectRetainer;
            return CraftingTasks.TaskResult.Retry;
        }

        if (GenericHelpers.TryGetAddonByName<AddonTalk>("Talk", out var talk) && talk->AtkUnitBase.IsVisible)
        {
            new AddonMaster.Talk((nint)talk).Click();
            Delay(300);
            return CraftingTasks.TaskResult.Retry;
        }

        _addonRetryCount++;
        if (_addonRetryCount > MaxAddonRetries)
        {
            GatherBuddy.Log.Warning("[RetainerTaskExecutor] Timed out waiting for RetainerList");
            _phase = Phase.Aborted;
            return CraftingTasks.TaskResult.Done;
        }

        Delay(150);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickSelectRetainer()
    {
        if (_retainerVisitIndex >= _retainersToVisit.Count)
        {
            _phase = Phase.CloseRetainerList;
            return CraftingTasks.TaskResult.Retry;
        }

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !addon->IsVisible)
        {
            Delay(150);
            return CraftingTasks.TaskResult.Retry;
        }

        var entry = _retainersToVisit[_retainerVisitIndex];
        Callback.Fire(addon, true, 2, (uint)entry.SortedIndex);
        _addonRetryCount = 0;
        _phase = Phase.WaitRetainerMenu;
        Delay(300);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitRetainerMenu()
    {
        if (GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var menu) &&
            menu->AtkUnitBase.IsVisible)
        {
            _phase = Phase.SelectEntrustWithdraw;
            _addonRetryCount = 0;
            Delay(200);
            return CraftingTasks.TaskResult.Retry;
        }

        _addonRetryCount++;
        if (_addonRetryCount > MaxAddonRetries)
        {
            GatherBuddy.Log.Warning("[RetainerTaskExecutor] Timed out waiting for retainer SelectString");
            _phase = Phase.Aborted;
            return CraftingTasks.TaskResult.Done;
        }

        Delay(150);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickSelectEntrustWithdraw()
    {
        if (!GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var menu) ||
            !menu->AtkUnitBase.IsVisible)
        {
            Delay(150);
            return CraftingTasks.TaskResult.Retry;
        }

        var entryCount = menu->PopupMenu.PopupMenu.EntryCount;
        if (entryCount == 0)
        {
            Delay(100);
            return CraftingTasks.TaskResult.Retry;
        }

        new AddonMaster.SelectString((nint)menu).Entries[0].Select();
        _addonRetryCount = 0;
        _phase = Phase.WaitInventory;
        Delay(200);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitInventory()
    {
        var agentRetainer = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        if (agentRetainer != null && agentRetainer->IsAgentActive())
        {
            var entry = _retainersToVisit[_retainerVisitIndex];
            _currentRetainerItems = BuildItemListForRetainer(entry.RetainerId);
            _currentItemIndex = 0;
            _addonRetryCount = 0;
            _phase = Phase.WithdrawNextItem;
            Delay(500);
            return CraftingTasks.TaskResult.Retry;
        }

        _addonRetryCount++;
        if (_addonRetryCount > MaxAddonRetries)
        {
            GatherBuddy.Log.Warning("[RetainerTaskExecutor] Timed out waiting for retainer inventory agent");
            _phase = Phase.CloseRetainerInventory;
            return CraftingTasks.TaskResult.Retry;
        }

        Delay(150);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWithdrawNextItem()
    {
        while (_currentItemIndex < _currentRetainerItems.Count)
        {
            var target = _currentRetainerItems[_currentItemIndex];
            if (target.Total <= 0)
            {
                _currentItemIndex++;
                continue;
            }

            if (target.RemainingHQ > 0)
            {
                _lookingForHQ = true;
                GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Withdrawing {target.RemainingHQ} HQ of item {target.ItemId}");
            }
            else
            {
                _lookingForHQ = false;
                GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Withdrawing {target.RemainingNQ} NQ of item {target.ItemId}");
            }

            _phase = Phase.FindItemSlot;
            return CraftingTasks.TaskResult.Retry;
        }

        _phase = Phase.CloseRetainerInventory;
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickFindItemSlot()
    {
        var target = _currentRetainerItems[_currentItemIndex];
        var itemId  = target.ItemId;

        var inventories = new[]
        {
            InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
            InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
            InventoryType.RetainerPage7, InventoryType.RetainerCrystals
        };

        foreach (var inv in inventories)
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(inv);
            if (container == null) continue;

            for (int i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId != itemId) continue;

                bool slotIsHQ = slot->Flags == InventoryItem.ItemFlags.HighQuality;
                if (slotIsHQ != _lookingForHQ) continue;

                _foundSlotQty = (int)slot->Quantity;
                GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Found item {itemId} (HQ={slotIsHQ}) in {inv} slot {i}, qty={_foundSlotQty}");

                var agentModule = AgentModule.Instance();
                if (agentModule == null) { _phase = Phase.Aborted; return CraftingTasks.TaskResult.Done; }

                var retainerAgentAddonId = agentModule->GetAgentByInternalId(AgentId.Retainer)->GetAddonId();
                AgentInventoryContext.Instance()->OpenForItemSlot(inv, i, 0, retainerAgentAddonId);

                _addonRetryCount = 0;
                _phase = Phase.WaitContextMenu;
                Delay(300);
                return CraftingTasks.TaskResult.Retry;
            }
        }

        GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Item {itemId} (HQ={_lookingForHQ}) not found in retainer inventory, skipping quality pass");

        var t = _currentRetainerItems[_currentItemIndex];
        if (_lookingForHQ && t.RemainingNQ > 0)
        {
            _lookingForHQ = false;
            _phase = Phase.FindItemSlot;
        }
        else
        {
            _currentItemIndex++;
            _phase = Phase.WithdrawNextItem;
        }

        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitContextMenu()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var menu) && menu->IsVisible)
        {
            _phase = Phase.SelectRetrieveOption;
            return CraftingTasks.TaskResult.Retry;
        }

        _addonRetryCount++;
        if (_addonRetryCount > 20)
        {
            GatherBuddy.Log.Warning("[RetainerTaskExecutor] ContextMenu did not appear, skipping item");
            _currentItemIndex++;
            _phase = Phase.WithdrawNextItem;
            return CraftingTasks.TaskResult.Retry;
        }

        Delay(100);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickOpenContextMenu()
    {
        _phase = Phase.WaitContextMenu;
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickSelectRetrieveOption()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var menu) || !menu->IsVisible)
        {
            _currentItemIndex++;
            _phase = Phase.WithdrawNextItem;
            return CraftingTasks.TaskResult.Retry;
        }

        var contextAgent  = AgentInventoryContext.Instance();
        var retrieveAll   = GetAddonText(98);
        var retrieveQty   = GetAddonText(773);
        int idxAll = -1, idxQty = -1, looper = 0;

        foreach (var param in contextAgent->EventParams)
        {
            if (param.Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String)
            {
                var label = MemoryHelper.ReadSeStringNullTerminated(new IntPtr(param.String)).TextValue;
                if (label == retrieveAll) idxAll = looper;
                if (label == retrieveQty) idxQty = looper;
                looper++;
            }
        }

        var target = _currentRetainerItems[_currentItemIndex];
        int wantQty = _lookingForHQ ? target.RemainingHQ : target.RemainingNQ;
        bool isCrystal = target.ItemId <= 19;

        if (isCrystal)
        {
            int crystalIdx = idxQty != -1 ? idxQty : idxAll != -1 ? idxAll : 0;
            GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Crystal retrieve (numeric dialog): index {crystalIdx}");
            Callback.Fire(menu, true, 0, crystalIdx, 0, 0, 0);
            _addonRetryCount = 0;
            _phase = Phase.WaitNumericInput;
            Delay(200);
            return CraftingTasks.TaskResult.Retry;
        }

        if (wantQty >= _foundSlotQty)
        {
            if (idxAll == -1)
            {
                GatherBuddy.Log.Warning("[RetainerTaskExecutor] 'Retrieve All' option not found in context menu");
                Callback.Fire(menu, true, 0, -1, 0, 0, 0);
                _currentItemIndex++;
                _phase = Phase.WithdrawNextItem;
                return CraftingTasks.TaskResult.Retry;
            }

            Callback.Fire(menu, true, 0, idxAll, 0, 0, 0);

            if (_lookingForHQ) target.RemainingHQ -= _foundSlotQty;
            else               target.RemainingNQ -= _foundSlotQty;
            _currentRetainerItems[_currentItemIndex] = target;

            _phase = Phase.WaitWithdrawComplete;
            Delay(300);
            return CraftingTasks.TaskResult.Retry;
        }

        if (idxQty == -1)
        {
            GatherBuddy.Log.Warning("[RetainerTaskExecutor] 'Retrieve (Quantity)' option not found in context menu");
            Callback.Fire(menu, true, 0, -1, 0, 0, 0);
            _currentItemIndex++;
            _phase = Phase.WithdrawNextItem;
            return CraftingTasks.TaskResult.Retry;
        }

        GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Retrieve quantity: index {idxQty}");
        Callback.Fire(menu, true, 0, idxQty, 0, 0, 0);
        _addonRetryCount = 0;
        _phase = Phase.WaitNumericInput;
        Delay(200);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitNumericInput()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("InputNumeric", out var input) && input->IsVisible)
        {
            _phase = Phase.InputNumericValue;
            return CraftingTasks.TaskResult.Retry;
        }

        _addonRetryCount++;
        if (_addonRetryCount > 20)
        {
            GatherBuddy.Log.Warning("[RetainerTaskExecutor] InputNumeric did not appear");
            _currentItemIndex++;
            _phase = Phase.WithdrawNextItem;
            return CraftingTasks.TaskResult.Retry;
        }

        Delay(100);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickInputNumericValue()
    {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("InputNumeric", out var input) || !input->IsVisible)
        {
            _currentItemIndex++;
            _phase = Phase.WithdrawNextItem;
            return CraftingTasks.TaskResult.Retry;
        }

        var target  = _currentRetainerItems[_currentItemIndex];
        int wantQty = _lookingForHQ ? target.RemainingHQ : target.RemainingNQ;
        int value   = Math.Min(wantQty, _foundSlotQty);

        GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Inputting quantity {value} (want={wantQty}, slot={_foundSlotQty})");
        Callback.Fire(input, true, value);

        if (_lookingForHQ) target.RemainingHQ -= value;
        else               target.RemainingNQ -= value;
        _currentRetainerItems[_currentItemIndex] = target;

        _phase = Phase.WaitWithdrawComplete;
        Delay(400);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitWithdrawComplete()
    {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("InputNumeric", out var input) && input->IsVisible)
        {
            Delay(100);
            return CraftingTasks.TaskResult.Retry;
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var ctx) && ctx->IsVisible)
        {
            Delay(100);
            return CraftingTasks.TaskResult.Retry;
        }

        var target = _currentRetainerItems[_currentItemIndex];

        if (_lookingForHQ && target.RemainingHQ > 0)
        {
            GatherBuddy.Log.Debug($"[RetainerTaskExecutor] More HQ needed ({target.RemainingHQ}), re-entering FindItemSlot");
            _phase = Phase.FindItemSlot;
            Delay(500);
            return CraftingTasks.TaskResult.Retry;
        }

        if (!_lookingForHQ && target.RemainingNQ > 0)
        {
            GatherBuddy.Log.Debug($"[RetainerTaskExecutor] More NQ needed ({target.RemainingNQ}), re-entering FindItemSlot");
            _phase = Phase.FindItemSlot;
            Delay(500);
            return CraftingTasks.TaskResult.Retry;
        }

        if (_lookingForHQ && target.RemainingHQ <= 0 && target.RemainingNQ > 0)
        {
            GatherBuddy.Log.Debug($"[RetainerTaskExecutor] HQ pass done, switching to NQ pass for item {target.ItemId}");
            _lookingForHQ = false;
            _phase = Phase.FindItemSlot;
            Delay(500);
            return CraftingTasks.TaskResult.Retry;
        }

        _currentItemIndex++;
        _phase = Phase.WithdrawNextItem;
        Delay(200);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickCloseRetainerInventory()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null) { _phase = Phase.SelectQuit; return CraftingTasks.TaskResult.Retry; }

        var retainerAgent = agentModule->GetAgentByInternalId(AgentId.Retainer);
        if (retainerAgent != null && retainerAgent->IsAgentActive())
        {
            retainerAgent->Hide();
            _addonRetryCount = 0;
            _phase = Phase.WaitInventoryClosed;
            Delay(300);
            return CraftingTasks.TaskResult.Retry;
        }

        _phase = Phase.SelectQuit;
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitInventoryClosed()
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null) { _phase = Phase.SelectQuit; return CraftingTasks.TaskResult.Retry; }
        var retainerAgent = agentModule->GetAgentByInternalId(AgentId.Retainer);
        if (retainerAgent == null || !retainerAgent->IsAgentActive())
        {
            _phase = Phase.SelectQuit;
            _addonRetryCount = 0;
            return CraftingTasks.TaskResult.Retry;
        }

        _addonRetryCount++;
        if (_addonRetryCount > 20)
        {
            GatherBuddy.Log.Warning("[RetainerTaskExecutor] Retainer agent did not close, continuing anyway");
            _phase = Phase.SelectQuit;
            return CraftingTasks.TaskResult.Retry;
        }

        Delay(150);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickSelectQuit()
    {
        if (!GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var menu) ||
            !menu->AtkUnitBase.IsVisible)
        {
            _addonRetryCount++;
            if (_addonRetryCount > 20)
            {
                GatherBuddy.Log.Warning("[RetainerTaskExecutor] SelectString not available for Quit, advancing to next retainer");
                _retainerVisitIndex++;
                _phase = Phase.SelectRetainer;
                _addonRetryCount = 0;
                return CraftingTasks.TaskResult.Retry;
            }
            Delay(150);
            return CraftingTasks.TaskResult.Retry;
        }

        var quitText  = GetAddonText(2383);
        var entryCount = menu->PopupMenu.PopupMenu.EntryCount;

        for (int i = 0; i < entryCount; i++)
        {
            var label = MemoryHelper.ReadSeStringNullTerminated((nint)menu->PopupMenu.PopupMenu.EntryNames[i].Value).TextValue;
            if (!label.Contains(quitText, StringComparison.OrdinalIgnoreCase))
                continue;

            new AddonMaster.SelectString((nint)menu).Entries[i].Select();
            _retainerVisitIndex++;
            _addonRetryCount = 0;
            _phase = Phase.WaitQuit;
            Delay(300);
            return CraftingTasks.TaskResult.Retry;
        }

        GatherBuddy.Log.Warning("[RetainerTaskExecutor] Could not find 'Quit' in retainer menu, advancing anyway");
        _retainerVisitIndex++;
        _phase = Phase.SelectRetainer;
        _addonRetryCount = 0;
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitQuit()
    {
        if (GenericHelpers.TryGetAddonByName<AddonTalk>("Talk", out var talk) && talk->AtkUnitBase.IsVisible)
        {
            GatherBuddy.Log.Debug("[RetainerTaskExecutor] Dismissing Talk dialog after Quit");
            new AddonMaster.Talk((nint)talk).Click();
            Delay(300);
            return CraftingTasks.TaskResult.Retry;
        }

        if (!GenericHelpers.TryGetAddonByName<AddonSelectString>("SelectString", out var menu) ||
            !menu->AtkUnitBase.IsVisible)
        {
            _phase = Phase.SelectRetainer;
            _addonRetryCount = 0;
            Delay(200);
            return CraftingTasks.TaskResult.Retry;
        }

        _addonRetryCount++;
        if (_addonRetryCount > 20)
        {
            GatherBuddy.Log.Warning("[RetainerTaskExecutor] Retainer menu did not close after Quit");
            _phase = Phase.SelectRetainer;
            _addonRetryCount = 0;
            return CraftingTasks.TaskResult.Retry;
        }

        Delay(150);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickCloseRetainerList()
    {
        if (GenericHelpers.TryGetAddonByName<AddonTalk>("Talk", out var talk) && talk->AtkUnitBase.IsVisible)
        {
            GatherBuddy.Log.Debug("[RetainerTaskExecutor] Dismissing Talk dialog before closing RetainerList");
            new AddonMaster.Talk((nint)talk).Click();
            Delay(300);
            return CraftingTasks.TaskResult.Retry;
        }

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !addon->IsVisible)
        {
            if (Dalamud.Conditions[ConditionFlag.OccupiedSummoningBell])
            {
                _addonRetryCount++;
                if (_addonRetryCount > MaxAddonRetries)
                {
                    GatherBuddy.Log.Warning("[RetainerTaskExecutor] Still at bell but RetainerList never appeared, marking complete");
                    _phase = Phase.Complete;
                    return CraftingTasks.TaskResult.Done;
                }
                Delay(150);
                return CraftingTasks.TaskResult.Retry;
            }

            _phase = Phase.Complete;
            return CraftingTasks.TaskResult.Done;
        }

        Callback.Fire(addon, true, -1);
        _addonRetryCount = 0;
        _phase = Phase.WaitListClosed;
        Delay(300);
        return CraftingTasks.TaskResult.Retry;
    }

    private CraftingTasks.TaskResult TickWaitListClosed()
    {
        if (GenericHelpers.TryGetAddonByName<AddonTalk>("Talk", out var talk) && talk->AtkUnitBase.IsVisible)
        {
            new AddonMaster.Talk((nint)talk).Click();
            Delay(300);
            return CraftingTasks.TaskResult.Retry;
        }

        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !addon->IsVisible)
        {
            _phase = Phase.Complete;
            return CraftingTasks.TaskResult.Done;
        }

        _addonRetryCount++;
        if (_addonRetryCount > 20)
        {
            GatherBuddy.Log.Warning("[RetainerTaskExecutor] RetainerList did not close, marking complete anyway");
            _phase = Phase.Complete;
            return CraftingTasks.TaskResult.Done;
        }

        Delay(150);
        return CraftingTasks.TaskResult.Retry;
    }

    private List<WithdrawTarget> BuildItemListForRetainer(ulong retainerId)
    {
        var list = new List<WithdrawTarget>();
        if (!_perRetainerPlan.TryGetValue(retainerId, out var items))
            return list;

        foreach (var (itemId, amounts) in items.OrderByDescending(kv => kv.Value.NeedHQ > 0 ? 1 : 0))
        {
            if (amounts.NeedHQ > 0 || amounts.NeedNQ > 0)
                list.Add(new WithdrawTarget(itemId, amounts.NeedHQ, amounts.NeedNQ));
        }

        GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Built item list for retainer {retainerId}: {list.Count} item(s)");
        return list;
    }

    private static HashSet<ulong> GetRetainerIdsFromManager()
    {
        var retainerIds = new HashSet<ulong>();
        var retainerMgr = RetainerManager.Instance();
        if (retainerMgr == null)
            return retainerIds;

        for (uint i = 0; i < retainerMgr->GetRetainerCount(); i++)
        {
            var retainer = retainerMgr->GetRetainerBySortedIndex(i);
            if (retainer == null || retainer->RetainerId == 0)
                continue;

            retainerIds.Add(retainer->RetainerId);
        }

        if (retainerIds.Count > 0)
            GatherBuddy.Log.Debug($"[RetainerTaskExecutor] Falling back to RetainerManager ids for {retainerIds.Count} retainer(s)");

        return retainerIds;
    }

    public static IGameObject? FindNearestBellForNavigation()
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null) return null;

        var bellName = GetBellName();
        IGameObject? nearest = null;
        float nearestDistance = float.MaxValue;

        foreach (var obj in Dalamud.Objects)
        {
            if (obj.ObjectKind != ObjectKind.Housing && obj.ObjectKind != ObjectKind.EventObj)
                continue;

            var name = obj.Name.TextValue;
            if (!name.Equals(bellName, StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("リテイナーベル", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!obj.IsTargetable)
                continue;

            var distance = Vector3.Distance(obj.Position, player.Position);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = obj;
            }
        }

        return nearest;
    }

    private static IGameObject? FindNearestBell()
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null) return null;

        var bellName = GetBellName();

        foreach (var obj in Dalamud.Objects)
        {
            if (obj.ObjectKind != ObjectKind.Housing && obj.ObjectKind != ObjectKind.EventObj)
                continue;

            var name = obj.Name.TextValue;
            if (!name.Equals(bellName, StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("リテイナーベル", StringComparison.OrdinalIgnoreCase))
                continue;

            float maxDist = obj.ObjectKind == ObjectKind.Housing ? 6.5f : 4.75f;
            if (Vector3.Distance(obj.Position, player.Position) > maxDist)
                continue;

            if (!obj.IsTargetable)
                continue;

            return obj;
        }

        return null;
    }

    private static string GetBellName()
    {
        try
        {
            var sheet = Dalamud.GameData.GetExcelSheet<EObjName>();
            if (sheet != null && sheet.TryGetRow(2000401, out var row))
                return row.Singular.ExtractText();
        }
        catch { }
        return "Summoning Bell";
    }

    private static string GetAddonText(uint rowId)
    {
        try
        {
            var sheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
            if (sheet != null && sheet.TryGetRow(rowId, out var row))
                return row.Text.ExtractText();
        }
        catch { }
        return string.Empty;
    }

    private void Delay(int ms)
    {
        _nextRetry = DateTime.Now.AddMilliseconds(ms);
    }


    internal static Dictionary<uint, IngredientQualityDemand> ComputeQualityTargets(
        Dictionary<uint, int> materials,
        List<CraftingListItem> expandedQueue)
    {
        var targets = materials.Keys.ToDictionary(itemId => itemId, _ => default(IngredientQualityDemand));

        foreach (var queueItem in expandedQueue)
        {
            var recipe = RecipeManager.GetRecipe(queueItem.RecipeId);
            if (recipe == null)
                continue;

            var qualityPolicy = queueItem.QualityPolicy ?? CraftingQualityPolicyResolver.Resolve(recipe.Value, queueItem.CraftSettings);
            foreach (var (itemId, _) in RecipeManager.GetIngredients(recipe.Value))
            {
                if (!targets.ContainsKey(itemId))
                    continue;

                targets[itemId] = targets[itemId].Add(qualityPolicy.GetDemand(itemId).Scale(Math.Max(1, queueItem.Quantity)));
            }
        }

        foreach (var (itemId, totalNeeded) in materials)
        {
            var target = targets[itemId];
            if (target.Total < totalNeeded)
            {
                targets[itemId] = target.Add(IngredientQualityDemand.FromPreferHQ(totalNeeded - target.Total));
                continue;
            }

            if (target.Total > totalNeeded)
                targets[itemId] = target.ConsumeUnknownQuality(target.Total - totalNeeded, out _);
        }

        return targets;
    }
}
