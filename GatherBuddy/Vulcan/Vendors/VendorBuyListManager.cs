using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.Helpers;
using GatherBuddy.Plugin;

namespace GatherBuddy.Vulcan.Vendors;

public sealed class VendorBuyListManager : IDisposable
{
    private readonly record struct VendorExecutionGroup(uint NpcId, VendorMenuShopType MenuShopType, uint ShopId);
    public readonly record struct GilShopTargetRequest(uint ItemId, uint TargetQuantity);
    private readonly record struct PendingEntrySelection(
        VendorBuyListEntry Entry,
        VendorShopEntry LiveEntry,
        VendorNpc Vendor,
        VendorNpcLocation Location,
        uint RemainingQuantity,
        int PriorityBucket,
        float DistanceSquared,
        uint RouteAetheryteId,
        int ListIndex);
    private readonly record struct VendorOrderingContext(uint TerritoryId, Vector3 Position, bool HasPosition, uint RouteAetheryteId);
    private enum PurchaseContextResolutionResult
    {
        Success,
        RetryableFailure,
        SkippableFailure,
    }
    private enum SameVendorContinuationResult
    {
        Started,
        NoCompatibleEntry,
        Failed,
    }
    private static readonly TimeSpan ShopCloseRetryDelay        = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ShopCloseTimeout           = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShopCloseBlockerLogDelay   = TimeSpan.FromSeconds(1);
    private const           string   DefaultVendorBuyListName   = "Default";

    private Guid?    _activeEntryId;
    private Guid?    _runningListId;
    private bool     _isRunning;
    private bool     _waitingForShopClose;
    private bool     _waitingForCancelledPurchase;
    private DateTime _shopCloseStartTime         = DateTime.MinValue;
    private DateTime _lastShopCloseAttemptTime   = DateTime.MinValue;
    private DateTime _lastShopCloseBlockerLogTime = DateTime.MinValue;
    private VendorExecutionGroup? _currentExecutionVendor;
    private readonly HashSet<Guid> _skippedEntryIds = new();
    private readonly HashSet<Guid> _partiallyFulfilledEntryIds = new();
    private string   _statusText = string.Empty;

    public VendorBuyListManager()
    {
        EnsureVendorCachesAvailable();
        EnsureListState();
        GatherBuddy.VendorPurchaseManager.PurchaseFinished += OnPurchaseFinished;
    }

    public IReadOnlyList<VendorBuyListDefinition> Lists
        => GatherBuddy.Config.VendorBuyLists;

    public VendorBuyListDefinition? ActiveList
        => GetActiveList();

    public IReadOnlyList<VendorBuyListEntry> Entries
        => ActiveList?.Entries ?? (IReadOnlyList<VendorBuyListEntry>)Array.Empty<VendorBuyListEntry>();

    public bool IsRunning
        => _isRunning;

    public bool IsBusy
        => _isRunning || _waitingForShopClose || _waitingForCancelledPurchase || _activeEntryId.HasValue;

    public Guid? ActiveEntryId
        => _activeEntryId;

    public Guid? RunningListId
        => _runningListId;

    public string ActiveListName
        => ActiveList?.Name ?? DefaultVendorBuyListName;

    public string StatusText
        => _statusText;

    public void Dispose()
    {
        Stop();
        GatherBuddy.VendorPurchaseManager.PurchaseFinished -= OnPurchaseFinished;
    }

    public void Update()
    {
        EnsureVendorCachesAvailable();
        if (!_waitingForShopClose)
            return;

        var blocker = VendorInteractionHelper.GetVendorExitBlocker();
        if (blocker == null)
        {
            ResetShopCloseWaitState();
            if (_isRunning)
                TryStartNextEntry();
            return;
        }

        if ((DateTime.UtcNow - _lastShopCloseAttemptTime) >= ShopCloseRetryDelay)
        {
            if (VendorInteractionHelper.TryExitVendorInteraction())
                _lastShopCloseAttemptTime = DateTime.UtcNow;
        }

        if ((DateTime.UtcNow - _lastShopCloseBlockerLogTime) >= ShopCloseBlockerLogDelay)
        {
            GatherBuddy.Log.Debug($"[VendorBuyListManager] Waiting for vendor interaction to close: {blocker}");
            _lastShopCloseBlockerLogTime = DateTime.UtcNow;
        }

        if ((DateTime.UtcNow - _shopCloseStartTime) <= ShopCloseTimeout)
            return;

        ResetShopCloseWaitState();
        ResetExecutionState();
        _statusText    = "Timed out leaving the previous vendor interaction.";
        GatherBuddy.Log.Error($"[VendorBuyListManager] Timed out leaving the previous vendor interaction. Last blocker: {blocker}");
        Communicator.PrintError("[GatherBuddyReborn] Timed out leaving the previous vendor interaction.");
    }

    public void OpenWindow()
        => GatherBuddy.VendorBuyListWindow?.Open();

    public bool CanAddGilShopItem(uint itemId)
        => VendorShopResolver.IsInitialized
        && VendorShopResolver.GilShopEntries.Any(entry => entry.ItemId == itemId);

    public VendorBuyListDefinition CreateList(string name = DefaultVendorBuyListName, bool setActive = true)
    {
        EnsureListState();

        var list = new VendorBuyListDefinition
        {
            Name = MakeUniqueListName(name),
        };
        GatherBuddy.Config.VendorBuyLists.Add(list);
        if (setActive)
            GatherBuddy.Config.ActiveVendorBuyListId = list.Id;
        GatherBuddy.Config.Save();

        if (!IsBusy)
            _statusText = $"Created vendor list '{list.Name}'.";

        GatherBuddy.Log.Information($"[VendorBuyListManager] Created vendor list '{list.Name}'");
        return list;
    }

    public bool SetActiveList(Guid listId)
    {
        EnsureListState();
        if (IsBusy)
            return false;

        if (!GatherBuddy.Config.VendorBuyLists.Any(list => list.Id == listId))
            return false;

        if (GatherBuddy.Config.ActiveVendorBuyListId == listId)
            return true;

        GatherBuddy.Config.ActiveVendorBuyListId = listId;
        GatherBuddy.Config.Save();
        _statusText = $"Selected vendor list '{ActiveListName}'.";
        return true;
    }

    public bool RenameList(Guid listId, string name)
    {
        EnsureListState();
        if (IsBusy)
            return false;

        var list = GetList(listId);
        if (list == null)
            return false;

        var updatedName = MakeUniqueListName(name, listId);
        if (list.Name.Equals(updatedName, StringComparison.Ordinal))
            return true;

        list.Name = updatedName;
        GatherBuddy.Config.Save();
        _statusText = $"Renamed vendor list to '{updatedName}'.";
        GatherBuddy.Log.Information($"[VendorBuyListManager] Renamed vendor list {listId} to '{updatedName}'");
        return true;
    }

    public bool DeleteList(Guid listId)
    {
        EnsureListState();
        if (IsBusy || GatherBuddy.Config.VendorBuyLists.Count <= 1)
            return false;

        var list = GetList(listId);
        if (list == null)
            return false;

        GatherBuddy.Config.VendorBuyLists.Remove(list);
        if (GatherBuddy.Config.ActiveVendorBuyListId == listId)
            GatherBuddy.Config.ActiveVendorBuyListId = GatherBuddy.Config.VendorBuyLists[0].Id;
        GatherBuddy.Config.Save();

        _statusText = $"Deleted vendor list '{list.Name}'.";
        GatherBuddy.Log.Information($"[VendorBuyListManager] Deleted vendor list '{list.Name}'");
        return true;
    }

    public bool TryAddTarget(VendorShopEntry entry, VendorNpc vendor, uint targetQuantity, bool openWindow = true, bool announce = false)
        => TryAddTarget(GetOrCreateActiveList(), entry, vendor, targetQuantity, false, openWindow, announce);

    public bool TryAddTarget(Guid listId, VendorShopEntry entry, VendorNpc vendor, uint targetQuantity, bool selectList = false,
        bool openWindow = true, bool announce = false)
    {
        EnsureListState();
        var list = GetList(listId);
        if (list == null)
            return false;

        return TryAddTarget(list, entry, vendor, targetQuantity, selectList, openWindow, announce);
    }

    public bool TryIncrementTarget(uint itemId, uint amount = 1, bool openWindow = true, bool announce = false)
        => TryIncrementTarget(GetOrCreateActiveList(), itemId, amount, false, openWindow, announce);

    public bool TryIncrementTarget(Guid listId, uint itemId, uint amount = 1, bool selectList = false, bool openWindow = true, bool announce = false)
    {
        EnsureListState();
        var list = GetList(listId);
        if (list == null)
            return false;

        return TryIncrementTarget(list, itemId, amount, selectList, openWindow, announce);
    }

    public bool TrySetGilShopTarget(Guid listId, uint itemId, uint targetQuantity, bool selectList = false, bool openWindow = true, bool announce = false)
        => TrySetGilShopTargets(listId, new[] { new GilShopTargetRequest(itemId, targetQuantity) }, selectList, openWindow, announce) > 0;

    public int TrySetGilShopTargets(Guid listId, IReadOnlyList<GilShopTargetRequest> requests, bool selectList = false, bool openWindow = true,
        bool announce = false)
    {
        EnsureListState();
        var list = GetList(listId);
        if (list == null)
            return 0;

        return TrySetGilShopTargets(list, requests, selectList, openWindow, announce);
    }

    public void UpdateTargetQuantity(Guid entryId, uint targetQuantity)
    {
        if (!TryFindEntry(entryId, out var list, out var entry) || list == null || entry == null)
            return;

        var clamped = Math.Max(1u, targetQuantity);
        if (entry.TargetQuantity == clamped)
            return;

        entry.TargetQuantity = clamped;
        GatherBuddy.Config.Save();

        if (!IsBusy)
            _statusText = $"{entry.ItemName} target set to {entry.TargetQuantity:N0} in '{list.Name}'.";
    }

    public bool SetEntryEnabled(Guid entryId, bool enabled)
    {
        if (IsBusy)
        {
            GatherBuddy.Log.Debug($"[VendorBuyListManager] Ignoring enabled-state update for vendor buy list entry {entryId} because the manager is busy.");
            return false;
        }

        if (!TryFindEntry(entryId, out var list, out var entry) || list == null || entry == null)
        {
            GatherBuddy.Log.Debug($"[VendorBuyListManager] Could not find vendor buy list entry {entryId} while updating its enabled state.");
            return false;
        }

        if (entry.Enabled == enabled)
            return true;

        entry.Enabled = enabled;
        GatherBuddy.Config.Save();
        _statusText = $"{(enabled ? "Enabled" : "Disabled")} {entry.ItemName} in '{list.Name}'.";
        return true;
    }

    public bool UpdateEntryVendor(Guid entryId, VendorNpc vendor)
    {
        if (IsBusy)
            return false;

        if (!TryFindEntry(entryId, out var list, out var entry) || list == null || entry == null)
            return false;

        if (!TryResolveLiveEntry(entry, out var liveEntry, out _, out _) || liveEntry == null)
        {
            GatherBuddy.Log.Debug($"[VendorBuyListManager] Could not resolve live vendor options for {entry.ItemName} while updating the vendor.");
            return false;
        }

        var selectedVendor = liveEntry.Npcs.FirstOrDefault(npc => VendorPreferenceHelper.MatchesVendor(npc, vendor));
        if (selectedVendor == null)
        {
            GatherBuddy.Log.Debug($"[VendorBuyListManager] Could not find vendor {vendor.NpcId}/{vendor.MenuShopType}/{vendor.ShopId}/{vendor.SourceShopId}:{vendor.ShopItemIndex} for {entry.ItemName} while updating the vendor.");
            return false;
        }
        if (MatchesVendor(entry, selectedVendor))
            return true;

        var existing = FindMatchingEntry(list, liveEntry, selectedVendor, entry.Id);
        if (existing != null)
        {
            var mergedTargetQuantity = Math.Max(existing.TargetQuantity, entry.TargetQuantity);
            UpdateEntry(existing, liveEntry, selectedVendor, mergedTargetQuantity);
            existing.Enabled |= entry.Enabled;
            list.Entries.Remove(entry);
            GatherBuddy.Config.Save();
            _statusText = $"Merged {entry.ItemName} onto vendor '{selectedVendor.Name}' in '{list.Name}'.";
            return true;
        }

        UpdateEntry(entry, liveEntry, selectedVendor, entry.TargetQuantity);
        GatherBuddy.Config.Save();
        _statusText = $"Changed {entry.ItemName} to vendor '{selectedVendor.Name}' in '{list.Name}'.";
        return true;
    }

    public void RemoveEntry(Guid entryId)
    {
        if (!TryFindEntry(entryId, out var list, out var entry) || list == null || entry == null)
            return;

        if (_isRunning && _activeEntryId == entryId)
            Stop();

        var removed = list.Entries.Remove(entry);
        if (!removed)
            return;

        GatherBuddy.Config.Save();
        if (!IsBusy)
            _statusText = $"Removed {entry.ItemName} from '{list.Name}'.";
    }

    public void Clear()
    {
        var list = ActiveList;
        if (list == null)
            return;

        Stop();
        if (list.Entries.Count == 0)
            return;

        list.Entries.Clear();
        GatherBuddy.Config.Save();
        _statusText = $"Cleared vendor list '{list.Name}'.";
    }

    public void Start()
    {
        EnsureListState();
        if (_isRunning)
            return;
        _skippedEntryIds.Clear();
        _partiallyFulfilledEntryIds.Clear();

        var activeList = ActiveList;
        if (activeList == null)
        {
            _statusText = "No vendor list is available.";
            return;
        }

        if (_waitingForShopClose)
        {
            _isRunning = true;
            _runningListId = activeList.Id;
            _statusText = $"Leaving the previous vendor interaction for '{activeList.Name}'.";
            return;
        }

        if (activeList.Entries.Count == 0)
        {
            _statusText = $"Vendor list '{activeList.Name}' is empty.";
            return;
        }

        if (GetPendingEntryCount(activeList) == 0)
        {
            _statusText = $"Vendor list '{activeList.Name}' has no enabled pending entries.";
            return;
        }

        if (!VendorShopResolver.IsInitialized)
        {
            EnsureVendorCachesAvailable();
            _statusText = "Vendor data is still loading.";
            return;
        }

        if (!VendorNpcLocationCache.IsInitialized)
        {
            EnsureVendorCachesAvailable();
            _statusText = VendorNpcLocationCache.IsInitializing
                ? "Vendor location data is still loading."
                : "Vendor location data is not ready yet.";
            return;
        }

        if (GatherBuddy.VendorPurchaseManager.IsRunning)
        {
            _statusText = "Another vendor purchase is already running.";
            return;
        }
        _currentExecutionVendor = null;

        _isRunning = true;
        _runningListId = activeList.Id;
        _waitingForCancelledPurchase = false;
        _statusText = $"Starting vendor list '{activeList.Name}'...";
        TryStartNextEntry();
    }

    public void Stop()
    {
        if (!_isRunning && !_waitingForCancelledPurchase && _activeEntryId == null && !_waitingForShopClose)
            return;
        var shouldStopPurchase = _activeEntryId.HasValue && GatherBuddy.VendorPurchaseManager.IsRunning;
        ResetExecutionState();
        _statusText = "Vendor list stopped.";
        _activeEntryId = null;
        _waitingForCancelledPurchase = shouldStopPurchase;

        if (shouldStopPurchase)
            GatherBuddy.VendorPurchaseManager.Stop();
        else if (!_waitingForShopClose)
            BeginShopCloseTransition("Vendor list stopped.");
    }

    public int GetPendingEntryCount()
        => GetPendingEntryCount(ActiveList);

    public uint GetRemainingQuantity(VendorBuyListEntry entry)
    {
        if (!entry.Enabled)
            return 0;
        var currentCount = (uint)Math.Max(0, GetCurrentInventoryAndArmoryCount(entry.ItemId));
        return entry.TargetQuantity > currentCount
            ? entry.TargetQuantity - currentCount
            : 0;
    }

    public bool TryResolveLiveEntry(VendorBuyListEntry entry, out VendorShopEntry? liveEntry, out VendorNpc? vendor, out VendorNpcLocation? location)
    {
        liveEntry = null;
        vendor    = null;
        location  = null;
        var candidates = GetMatchingCandidates(entry);
        if (candidates.Count == 0)
            return false;

        liveEntry = candidates.FirstOrDefault(candidate => candidate.Npcs.Any(npc =>
                MatchesVendor(entry, npc)))
            ?? candidates.FirstOrDefault(candidate => candidate.Npcs.Any(npc =>
                MatchesVendor(entry, npc, false)))
            ?? candidates.FirstOrDefault(candidate => candidate.Npcs.Any(npc => VendorPurchaseManager.IsPurchaseSupported(candidate, npc)))
            ?? candidates[0];

        var resolvedLiveEntry = liveEntry;
        var supportedVendors = VendorDevExclusions.GetSelectableNpcs(
                resolvedLiveEntry.Npcs
                    .Where(npc => VendorPurchaseManager.IsPurchaseSupported(resolvedLiveEntry, npc))
                    .ToList(),
                "resolving a vendor buy list entry",
                entry.ItemName)
            .ToList();
        if (supportedVendors.Count == 0)
            return false;
        vendor = supportedVendors.FirstOrDefault(npc => MatchesVendor(entry, npc))
            ?? supportedVendors.FirstOrDefault(npc => MatchesVendor(entry, npc, false))
            ?? VendorPreferenceHelper.ResolvePreferredNpc(resolvedLiveEntry, supportedVendors)
            ?? VendorPreferenceHelper.ResolvePreferredNpc(entry, supportedVendors)
            ?? supportedVendors.FirstOrDefault();
        if (vendor == null)
            return false;

        location = VendorNpcLocationCache.TryGetFirstLocation(vendor.NpcId);
        if (location == null)
        {
            var fallbackVendor = VendorPreferenceHelper.ResolvePreferredNpc(resolvedLiveEntry, supportedVendors)
                ?? VendorPreferenceHelper.ResolvePreferredNpc(entry, supportedVendors)
                ?? supportedVendors.FirstOrDefault();
            if (fallbackVendor != null)
            {
                vendor   = fallbackVendor;
                location = VendorNpcLocationCache.TryGetFirstLocation(vendor.NpcId);
            }
        }

        return true;
    }

    public static int GetCurrentInventoryAndArmoryCount(uint itemId)
        => ItemHelper.GetInventoryAndArmoryItemCount(itemId);

    private int GetPendingEntryCount(VendorBuyListDefinition? list)
        => list?.Entries.Count(entry => GetRemainingQuantity(entry) > 0) ?? 0;

    private void ResetExecutionState()
    {
        _isRunning = false;
        _activeEntryId = null;
        _runningListId = null;
        _currentExecutionVendor = null;
        _skippedEntryIds.Clear();
        _partiallyFulfilledEntryIds.Clear();
    }

    private bool IsDeferredForCurrentRun(VendorBuyListEntry entry)
        => _skippedEntryIds.Contains(entry.Id) || _partiallyFulfilledEntryIds.Contains(entry.Id);

    private void SkipEntryForCurrentRun(VendorBuyListEntry entry, string message, bool announceFailure)
    {
        if (!_skippedEntryIds.Add(entry.Id))
            return;

        GatherBuddy.Log.Debug($"[VendorBuyListManager] Skipping {entry.ItemName} for the current vendor-list run: {message}");
        if (announceFailure)
            Communicator.PrintError($"[GatherBuddyReborn] {message}");
    }

    private void MarkEntryPartiallyFulfilledForCurrentRun(VendorBuyListEntry entry, string message)
    {
        if (!_partiallyFulfilledEntryIds.Add(entry.Id))
            return;

        GatherBuddy.Log.Debug($"[VendorBuyListManager] Deferring the remaining target for {entry.ItemName} until a future vendor-list run after a partial purchase: {message}");
    }

    private void FailCurrentRun(string message)
    {
        ResetExecutionState();
        _statusText = message;
    }

    private void FinishCurrentRun(VendorBuyListDefinition list)
    {
        var skippedCount = _skippedEntryIds.Count;
        var partiallyFulfilledCount = _partiallyFulfilledEntryIds.Count;
        ResetExecutionState();
        if (skippedCount == 0 && partiallyFulfilledCount == 0)
        {
            _statusText = $"Vendor list '{list.Name}' complete.";
            GatherBuddy.Log.Information($"[VendorBuyListManager] Vendor list '{list.Name}' complete.");
            Communicator.Print($"[GatherBuddyReborn] Vendor list '{list.Name}' complete.");
            return;
        }
        var resultParts = new List<string>();
        if (partiallyFulfilledCount > 0)
            resultParts.Add($"{partiallyFulfilledCount} partially fulfilled entr{(partiallyFulfilledCount == 1 ? "y" : "ies")}");
        if (skippedCount > 0)
            resultParts.Add($"{skippedCount} skipped entr{(skippedCount == 1 ? "y" : "ies")}");

        var detail = string.Join(" and ", resultParts);
        _statusText = $"Vendor list '{list.Name}' completed with {detail}.";
        GatherBuddy.Log.Warning($"[VendorBuyListManager] Vendor list '{list.Name}' completed with {detail}.");
        Communicator.PrintError($"[GatherBuddyReborn] Vendor list '{list.Name}' completed with {detail}.");
    }

    private void EnsureListState()
    {
        if (GatherBuddy.Config.EnsureVendorBuyListState())
            GatherBuddy.Config.Save();
    }

    private VendorBuyListDefinition GetOrCreateActiveList()
    {
        EnsureListState();
        return ActiveList ?? CreateList(DefaultVendorBuyListName);
    }

    private VendorBuyListDefinition? GetActiveList()
    {
        EnsureListState();
        return GetList(GatherBuddy.Config.ActiveVendorBuyListId)
            ?? GatherBuddy.Config.VendorBuyLists.FirstOrDefault();
    }

    private VendorBuyListDefinition? GetList(Guid listId)
        => GatherBuddy.Config.VendorBuyLists.FirstOrDefault(list => list.Id == listId);

    private static List<VendorShopEntry> GetMatchingCandidates(VendorBuyListEntry entry)
    {
        var candidates = GetCandidateEntries(entry)
            .Where(candidate => candidate.ItemId == entry.ItemId
                && candidate.Cost == entry.Cost)
            .ToList();
        if (entry.ShopType != VendorShopType.GrandCompanySeals)
            return candidates
                .Where(candidate => candidate.CurrencyItemId == entry.CurrencyItemId)
                .ToList();

        if (entry.GcRankIndex >= 0)
            candidates = candidates
                .Where(candidate => candidate.Npcs.Any(npc => npc.GcRankIndex == entry.GcRankIndex))
                .ToList();
        if (entry.GcCategoryIndex >= 0)
            candidates = candidates
                .Where(candidate => candidate.Npcs.Any(npc => npc.GcCategoryIndex == entry.GcCategoryIndex))
                .ToList();

        var currentCurrencyItemId = VendorShopResolver.GetCurrentGrandCompanySealCurrencyItemId();
        if (currentCurrencyItemId != 0)
            return candidates
                .Where(candidate => candidate.CurrencyItemId == currentCurrencyItemId)
                .ToList();

        var savedCurrencyCandidates = candidates
            .Where(candidate => candidate.CurrencyItemId == entry.CurrencyItemId)
            .ToList();
        return savedCurrencyCandidates.Count > 0
            ? savedCurrencyCandidates
            : candidates;
    }

    private bool TryAddTarget(VendorBuyListDefinition list, VendorShopEntry entry, VendorNpc vendor, uint targetQuantity, bool selectList,
        bool openWindow, bool announce)
    {
        if (!TrySetResolvedTarget(list, entry, vendor, targetQuantity))
            return false;
        if (selectList)
            GatherBuddy.Config.ActiveVendorBuyListId = list.Id;
        GatherBuddy.Config.Save();

        if (openWindow)
            OpenWindow();

        if (!IsBusy)
            _statusText = $"{entry.ItemName} target set to {targetQuantity:N0} in '{list.Name}'.";

        if (announce)
            Communicator.Print($"[GatherBuddyReborn] Added {entry.ItemName} to '{list.Name}' with target {targetQuantity:N0}.");
        return true;
    }

    private bool TrySetResolvedTarget(VendorBuyListDefinition list, VendorShopEntry entry, VendorNpc vendor, uint targetQuantity)
    {
        if (!VendorPurchaseManager.IsPurchaseSupported(entry, vendor))
            return false;

        targetQuantity = Math.Max(1u, targetQuantity);

        var existing = FindMatchingEntry(list, entry, vendor);
        if (existing == null)
        {
            existing = new VendorBuyListEntry();
            list.Entries.Add(existing);
        }

        UpdateEntry(existing, entry, vendor, targetQuantity);
        existing.Enabled = true;
        return true;
    }

    private bool TryIncrementTarget(VendorBuyListDefinition list, uint itemId, uint amount, bool selectList, bool openWindow, bool announce)
    {
        amount = Math.Max(1u, amount);

        var existing = list.Entries
            .FirstOrDefault(entry => entry.ItemId == itemId && entry.ShopType == VendorShopType.GilShop);
        if (existing != null)
        {
            existing.TargetQuantity = SaturatingAdd(Math.Max(existing.TargetQuantity, (uint)Math.Max(0, GetCurrentInventoryAndArmoryCount(itemId))), amount);
            existing.Enabled = true;
            if (selectList)
                GatherBuddy.Config.ActiveVendorBuyListId = list.Id;
            GatherBuddy.Config.Save();

            if (openWindow)
                OpenWindow();

            if (!IsBusy)
                _statusText = $"{existing.ItemName} target set to {existing.TargetQuantity:N0} in '{list.Name}'.";

            if (announce)
                Communicator.Print($"[GatherBuddyReborn] Added {existing.ItemName} to '{list.Name}' with target {existing.TargetQuantity:N0}.");
            return true;
        }

        if (!TryResolveDefaultEntry(itemId, out var liveEntry, out var vendor))
            return false;

        var targetQuantity = SaturatingAdd((uint)Math.Max(0, GetCurrentInventoryAndArmoryCount(itemId)), amount);
        return TryAddTarget(list, liveEntry, vendor, targetQuantity, selectList, openWindow, announce);
    }

    private int TrySetGilShopTargets(VendorBuyListDefinition list, IReadOnlyList<GilShopTargetRequest> requests, bool selectList, bool openWindow,
        bool announce)
    {
        var normalizedRequests = requests
            .Where(request => request.ItemId != 0 && request.TargetQuantity > 0)
            .GroupBy(request => request.ItemId)
            .Select(group => new GilShopTargetRequest(group.Key, group.Max(request => request.TargetQuantity)))
            .ToList();
        if (normalizedRequests.Count == 0)
            return 0;

        var addedTargets = new List<(VendorShopEntry Entry, uint TargetQuantity)>();
        foreach (var request in normalizedRequests)
        {
            if (!TryResolveDefaultEntry(request.ItemId, out var liveEntry, out var vendor))
            {
                GatherBuddy.Log.Debug($"[VendorBuyListManager] Could not resolve a default gil-shop entry for item {request.ItemId} while setting vendor targets.");
                continue;
            }

            if (!TrySetResolvedTarget(list, liveEntry, vendor, request.TargetQuantity))
            {
                GatherBuddy.Log.Debug($"[VendorBuyListManager] Could not set a gil-shop target for {liveEntry.ItemName} in '{list.Name}'.");
                continue;
            }

            addedTargets.Add((liveEntry, request.TargetQuantity));
        }

        if (addedTargets.Count == 0)
            return 0;

        if (selectList)
            GatherBuddy.Config.ActiveVendorBuyListId = list.Id;
        GatherBuddy.Config.Save();

        if (openWindow)
            OpenWindow();

        if (!IsBusy)
            _statusText = addedTargets.Count == 1
                ? $"{addedTargets[0].Entry.ItemName} target set to {addedTargets[0].TargetQuantity:N0} in '{list.Name}'."
                : $"{addedTargets.Count:N0} vendor targets updated in '{list.Name}'.";

        if (announce)
        {
            if (addedTargets.Count == 1)
                Communicator.Print($"[GatherBuddyReborn] Added {addedTargets[0].Entry.ItemName} to '{list.Name}' with target {addedTargets[0].TargetQuantity:N0}.");
            else
                Communicator.Print($"[GatherBuddyReborn] Added {addedTargets.Count:N0} items to '{list.Name}'.");
        }

        return addedTargets.Count;
    }

    private VendorBuyListDefinition? GetExecutionList()
        => _runningListId.HasValue
            ? GetList(_runningListId.Value)
            : ActiveList;

    private bool TryFindEntry(Guid entryId, out VendorBuyListDefinition? list, out VendorBuyListEntry? entry)
    {
        foreach (var currentList in GatherBuddy.Config.VendorBuyLists)
        {
            var currentEntry = currentList.Entries.FirstOrDefault(item => item.Id == entryId);
            if (currentEntry == null)
                continue;

            list  = currentList;
            entry = currentEntry;
            return true;
        }

        list  = null;
        entry = null;
        return false;
    }

    private string MakeUniqueListName(string name, Guid? excludeListId = null)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? DefaultVendorBuyListName : name.Trim();
        var uniqueName = baseName;
        var suffix = 1;
        while (GatherBuddy.Config.VendorBuyLists.Any(list =>
                   list.Name.Equals(uniqueName, StringComparison.OrdinalIgnoreCase)
                && (!excludeListId.HasValue || list.Id != excludeListId.Value)))
        {
            uniqueName = $"{baseName} ({suffix})";
            suffix++;
        }

        return uniqueName;
    }

    private static uint SaturatingAdd(uint left, uint right)
        => left > uint.MaxValue - right ? uint.MaxValue : left + right;

    private static bool MatchesVendor(VendorBuyListEntry entry, VendorNpc vendor, bool includeRoute = true)
        => entry.VendorNpcId == vendor.NpcId
        && entry.MenuShopType == vendor.MenuShopType
        && entry.ShopId == vendor.ShopId
        && (!includeRoute
         || entry.ShopType switch
         {
             VendorShopType.SpecialCurrency => (entry.SourceShopId == 0 || entry.SourceShopId == vendor.SourceShopId)
                                           && (entry.ShopItemIndex < 0 || entry.ShopItemIndex == vendor.ShopItemIndex),
             VendorShopType.GrandCompanySeals => (entry.GcRankIndex < 0 || entry.GcRankIndex == vendor.GcRankIndex)
                                             && (entry.GcCategoryIndex < 0 || entry.GcCategoryIndex == vendor.GcCategoryIndex),
             _ => true,
         });

    private static VendorBuyListEntry? FindMatchingEntry(VendorBuyListDefinition list, VendorShopEntry entry, VendorNpc vendor, Guid? excludeEntryId = null)
        => list.Entries.FirstOrDefault(existing =>
            (!excludeEntryId.HasValue || existing.Id != excludeEntryId.Value)
         && existing.ShopType == entry.ShopType
         && existing.ItemId == entry.ItemId
         && existing.Cost == entry.Cost
         && (entry.ShopType == VendorShopType.GrandCompanySeals
             ? existing.GcRankIndex == vendor.GcRankIndex
            && existing.GcCategoryIndex == vendor.GcCategoryIndex
             : existing.ShopType == entry.ShopType
            && existing.SourceShopId == vendor.SourceShopId
            && existing.ShopItemIndex == vendor.ShopItemIndex
            && existing.GcRankIndex == vendor.GcRankIndex
            && existing.GcCategoryIndex == vendor.GcCategoryIndex
            && existing.ItemId == entry.ItemId
            && existing.CurrencyItemId == entry.CurrencyItemId
            && existing.Cost == entry.Cost
            && existing.VendorNpcId == vendor.NpcId
            && existing.MenuShopType == vendor.MenuShopType
            && existing.ShopId == vendor.ShopId));

    private static void UpdateEntry(VendorBuyListEntry target, VendorShopEntry entry, VendorNpc vendor, uint targetQuantity)
    {
        target.ItemId         = entry.ItemId;
        target.ItemName       = entry.ItemName;
        target.IconId         = entry.IconId;
        target.Cost           = entry.Cost;
        target.CurrencyItemId = entry.CurrencyItemId;
        target.CurrencyName   = entry.CurrencyName;
        target.ShopType       = entry.ShopType;
        target.SourceShopId   = vendor.SourceShopId;
        target.ShopItemIndex  = vendor.ShopItemIndex;
        target.GcRankIndex    = vendor.GcRankIndex;
        target.GcCategoryIndex = vendor.GcCategoryIndex;
        target.VendorNpcId    = vendor.NpcId;
        target.VendorNpcName  = vendor.Name;
        target.MenuShopType   = vendor.MenuShopType;
        target.ShopId         = vendor.ShopId;
        target.TargetQuantity = Math.Max(1u, targetQuantity);
    }

    private static IEnumerable<VendorShopEntry> GetCandidateEntries(VendorBuyListEntry entry)
        => entry.ShopType switch
        {
            VendorShopType.GilShop           => VendorShopResolver.GilShopEntries,
            VendorShopType.SpecialCurrency   => VendorShopResolver.SpecialShopEntries,
            VendorShopType.GrandCompanySeals => VendorShopResolver.GcShopEntries,
            _                                => Enumerable.Empty<VendorShopEntry>(),
        };

    private bool TryResolveDefaultEntry(uint itemId, out VendorShopEntry entry, out VendorNpc vendor)
    {
        entry  = null!;
        vendor = null!;

        if (!VendorShopResolver.IsInitialized)
            return false;

        foreach (var candidate in VendorShopResolver.GilShopEntries.Where(candidate => candidate.ItemId == itemId).OrderBy(candidate => candidate.Cost))
        {
            var preferredVendor = VendorPreferenceHelper.ResolvePreferredNpc(candidate);
            if (preferredVendor == null)
                continue;

            entry  = candidate;
            vendor = preferredVendor;
            return true;
        }

        return false;
    }

    private PendingEntrySelection? GetNextPendingEntry(VendorBuyListDefinition list)
    {
        var orderingContext = CaptureVendorOrderingContext();
        PendingEntrySelection? bestSelection = null;

        for (var index = 0; index < list.Entries.Count; index++)
        {
            var entry = list.Entries[index];
            if (IsDeferredForCurrentRun(entry))
                continue;

            var remainingQuantity = GetRemainingQuantity(entry);
            if (remainingQuantity == 0)
                continue;

            var resolutionResult = TryResolvePurchaseContext(entry, out var liveEntry, out var vendor, out var location, out var errorMessage);
            if (resolutionResult != PurchaseContextResolutionResult.Success)
            {
                HandleEntryResolutionFailure(entry, errorMessage, resolutionResult);
                if (!_isRunning)
                    return null;
                continue;
            }

            var routeAetheryteId = VendorNavigator.GetPrimaryRouteAetheryteId(location.TerritoryId, location.Position);
            var priorityBucket = GetVendorOrderingPriorityBucket(orderingContext, location, routeAetheryteId);
            var distanceSquared = GetVendorOrderingDistanceSquared(orderingContext, location, priorityBucket);
            var candidate = new PendingEntrySelection(entry, liveEntry, vendor, location, remainingQuantity, priorityBucket, distanceSquared,
                routeAetheryteId, index);
            if (!bestSelection.HasValue || ComparePendingEntrySelections(candidate, bestSelection.Value) < 0)
                bestSelection = candidate;
        }

        if (bestSelection is not { } selected)
            return null;

        var distanceText = selected.DistanceSquared < float.MaxValue
            ? $"{MathF.Sqrt(selected.DistanceSquared):F1}m"
            : "route-ranked";
        GatherBuddy.Log.Debug(
            $"[VendorBuyListManager] Selected {selected.Entry.ItemName} from {selected.Vendor.Name} next using ordering bucket {selected.PriorityBucket} (territory={selected.Location.TerritoryId}, route={selected.RouteAetheryteId}, distance={distanceText}).");
        return selected;
    }

    private static VendorOrderingContext CaptureVendorOrderingContext()
    {
        var territoryId = Player.Territory;
        if (!Player.Available)
            return new VendorOrderingContext(territoryId, Vector3.Zero, false, 0);

        var position = Player.Position;
        return new VendorOrderingContext(
            territoryId,
            position,
            position != Vector3.Zero,
            VendorNavigator.GetPrimaryRouteAetheryteId(territoryId, position));
    }

    private static int GetVendorOrderingPriorityBucket(VendorOrderingContext orderingContext, VendorNpcLocation location, uint routeAetheryteId)
    {
        if (orderingContext.TerritoryId == location.TerritoryId)
            return 0;
        if (VendorNavigator.IsEquivalentTerritory(orderingContext.TerritoryId, location.TerritoryId))
            return 1;
        if (orderingContext.RouteAetheryteId != 0 && routeAetheryteId != 0 && orderingContext.RouteAetheryteId == routeAetheryteId)
            return 2;
        return 3;
    }

    private static float GetVendorOrderingDistanceSquared(VendorOrderingContext orderingContext, VendorNpcLocation location, int priorityBucket)
    {
        if (!orderingContext.HasPosition || priorityBucket != 0)
            return float.MaxValue;

        var playerPosition = new Vector2(orderingContext.Position.X, orderingContext.Position.Z);
        var vendorPosition = new Vector2(location.Position.X, location.Position.Z);
        return Vector2.DistanceSquared(playerPosition, vendorPosition);
    }

    private static int ComparePendingEntrySelections(PendingEntrySelection left, PendingEntrySelection right)
    {
        var priorityComparison = left.PriorityBucket.CompareTo(right.PriorityBucket);
        if (priorityComparison != 0)
            return priorityComparison;

        var distanceComparison = left.DistanceSquared.CompareTo(right.DistanceSquared);
        if (distanceComparison != 0)
            return distanceComparison;

        var leftRouteAetheryteId = left.RouteAetheryteId == 0 ? uint.MaxValue : left.RouteAetheryteId;
        var rightRouteAetheryteId = right.RouteAetheryteId == 0 ? uint.MaxValue : right.RouteAetheryteId;
        var routeComparison = leftRouteAetheryteId.CompareTo(rightRouteAetheryteId);
        if (routeComparison != 0)
            return routeComparison;

        var territoryComparison = left.Location.TerritoryId.CompareTo(right.Location.TerritoryId);
        if (territoryComparison != 0)
            return territoryComparison;

        var mapComparison = left.Location.MapRowId.CompareTo(right.Location.MapRowId);
        if (mapComparison != 0)
            return mapComparison;

        var xComparison = left.Location.Position.X.CompareTo(right.Location.Position.X);
        if (xComparison != 0)
            return xComparison;

        var zComparison = left.Location.Position.Z.CompareTo(right.Location.Position.Z);
        if (zComparison != 0)
            return zComparison;

        return left.ListIndex.CompareTo(right.ListIndex);
    }

    private bool TryStartResolvedEntry(VendorBuyListDefinition list, VendorBuyListEntry entry, VendorShopEntry liveEntry, VendorNpc vendor, VendorNpcLocation location,
        uint remainingQuantity, bool continueCurrentVendorInteraction)
    {
        _currentExecutionVendor = new VendorExecutionGroup(vendor.NpcId, vendor.MenuShopType, vendor.ShopId);
        _activeEntryId = entry.Id;
        _statusText = $"Buying {remainingQuantity:N0}x {entry.ItemName} from {vendor.Name} in '{list.Name}'.";
        GatherBuddy.VendorPurchaseManager.StartPurchase(liveEntry, vendor, location, remainingQuantity, continueCurrentVendorInteraction);
        if (GatherBuddy.VendorPurchaseManager.IsRunning)
            return true;

        _activeEntryId = null;
        if (!continueCurrentVendorInteraction)
            _currentExecutionVendor = null;

        SkipEntryForCurrentRun(entry, $"Failed to start vendor purchase for {entry.ItemName}. Skipping it for the current run.", true);
        return false;
    }

    private void HandleEntryResolutionFailure(VendorBuyListEntry entry, string errorMessage, PurchaseContextResolutionResult resolutionResult)
    {
        switch (resolutionResult)
        {
            case PurchaseContextResolutionResult.RetryableFailure:
                FailCurrentRun(errorMessage);
                break;
            case PurchaseContextResolutionResult.SkippableFailure:
                SkipEntryForCurrentRun(entry, $"{errorMessage} Skipping it for the current run.", true);
                break;
        }
    }

    private SameVendorContinuationResult TryContinueWithCurrentVendor(VendorExecutionGroup vendorGroup)
    {
        if (!_isRunning)
            return SameVendorContinuationResult.NoCompatibleEntry;

        var list = GetExecutionList();
        if (list == null)
        {
            FailCurrentRun("The active vendor list is no longer available.");
            return SameVendorContinuationResult.Failed;
        }

        foreach (var entry in list.Entries)
        {
            if (IsDeferredForCurrentRun(entry))
                continue;

            var remainingQuantity = GetRemainingQuantity(entry);
            if (remainingQuantity == 0)
                continue;

            var resolutionResult = TryResolvePurchaseContext(entry, out var liveEntry, out var vendor, out var location, out var errorMessage);
            if (resolutionResult != PurchaseContextResolutionResult.Success)
            {
                HandleEntryResolutionFailure(entry, errorMessage, resolutionResult);
                if (!_isRunning)
                    return SameVendorContinuationResult.Failed;
                continue;
            }

            var resolvedVendorGroup = new VendorExecutionGroup(vendor.NpcId, vendor.MenuShopType, vendor.ShopId);
            if (resolvedVendorGroup != vendorGroup)
                continue;

            if (TryStartResolvedEntry(list, entry, liveEntry, vendor, location, remainingQuantity, true))
                return SameVendorContinuationResult.Started;
        }

        return SameVendorContinuationResult.NoCompatibleEntry;
    }

    private void TryStartNextEntry()
    {
        if (!_isRunning)
            return;

        var list = GetExecutionList();
        if (list == null)
        {
            FailCurrentRun("The active vendor list is no longer available.");
            return;
        }

        while (_isRunning)
        {
            var nextSelection = GetNextPendingEntry(list);
            if (nextSelection == null)
            {
                if (!_isRunning)
                    return;
                FinishCurrentRun(list);
                return;
            }
            if (TryStartResolvedEntry(list, nextSelection.Value.Entry, nextSelection.Value.LiveEntry, nextSelection.Value.Vendor,
                    nextSelection.Value.Location, nextSelection.Value.RemainingQuantity, false))
                return;
        }
    }

    private PurchaseContextResolutionResult TryResolvePurchaseContext(VendorBuyListEntry entry, out VendorShopEntry liveEntry, out VendorNpc vendor, out VendorNpcLocation location, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!VendorNpcLocationCache.IsInitialized)
        {
            EnsureVendorCachesAvailable();
            liveEntry    = null!;
            vendor       = null!;
            location     = null!;
            errorMessage = VendorNpcLocationCache.IsInitializing
                ? "Vendor location data is still loading."
                : "Vendor location data is not ready yet.";
            return PurchaseContextResolutionResult.RetryableFailure;
        }

        if (!TryResolveLiveEntry(entry, out var resolvedEntry, out var resolvedVendor, out var resolvedLocation)
         || resolvedEntry == null
         || resolvedVendor == null)
        {
            liveEntry    = null!;
            vendor       = null!;
            location     = null!;
            errorMessage = $"Could not resolve {entry.ItemName} in the {DescribeShopType(entry.ShopType)} vendor data.";
            return PurchaseContextResolutionResult.SkippableFailure;
        }

        if (resolvedLocation == null)
        {
            liveEntry    = null!;
            vendor       = null!;
            location     = null!;
            errorMessage = $"No vendor location data is available for {resolvedVendor.Name}.";
            return PurchaseContextResolutionResult.SkippableFailure;
        }

        if (entry.ShopType == VendorShopType.GrandCompanySeals
         && (entry.CurrencyItemId != resolvedEntry.CurrencyItemId || !MatchesVendor(entry, resolvedVendor)))
        {
            UpdateEntry(entry, resolvedEntry, resolvedVendor, entry.TargetQuantity);
            GatherBuddy.Config.Save();
        }

        liveEntry = resolvedEntry;
        vendor    = resolvedVendor;
        location  = resolvedLocation;
        return PurchaseContextResolutionResult.Success;
    }

    private void OnPurchaseFinished(VendorPurchaseManager.PurchaseResult result)
    {
        if (_waitingForCancelledPurchase && result.State == VendorPurchaseManager.CompletionState.Cancelled)
        {
            _waitingForCancelledPurchase = false;
            _currentExecutionVendor = null;
            BeginShopCloseTransition("Vendor list stopped.");
            return;
        }

        if (!_isRunning)
            return;

        switch (result.State)
        {
            case VendorPurchaseManager.CompletionState.Completed:
                _activeEntryId = null;
                switch (TryContinueWithCurrentVendor(new VendorExecutionGroup(result.Vendor.NpcId, result.Vendor.MenuShopType, result.Vendor.ShopId)))
                {
                    case SameVendorContinuationResult.Started:
                        return;
                    case SameVendorContinuationResult.Failed:
                        BeginShopCloseTransition(_statusText);
                        return;
                }
                BeginShopCloseTransition($"Leaving {result.Vendor.Name}.");
                break;
            case VendorPurchaseManager.CompletionState.PartiallyCompleted:
                if (_activeEntryId is { } partiallyFulfilledEntryId
                 && TryFindEntry(partiallyFulfilledEntryId, out _, out var partiallyFulfilledEntry)
                 && partiallyFulfilledEntry != null)
                    MarkEntryPartiallyFulfilledForCurrentRun(partiallyFulfilledEntry, result.Message);
                _activeEntryId = null;

                if (!VendorInteractionHelper.IsReadyToLeaveVendor())
                {
                    switch (TryContinueWithCurrentVendor(new VendorExecutionGroup(result.Vendor.NpcId, result.Vendor.MenuShopType, result.Vendor.ShopId)))
                    {
                        case SameVendorContinuationResult.Started:
                            return;
                        case SameVendorContinuationResult.Failed:
                            BeginShopCloseTransition(_statusText);
                            return;
                    }
                }

                BeginShopCloseTransition($"Continuing the vendor list after partially purchasing {result.ItemName}.");
                break;
            case VendorPurchaseManager.CompletionState.Cancelled:
                ResetExecutionState();
                BeginShopCloseTransition("Leaving vendor interaction.");
                break;
            case VendorPurchaseManager.CompletionState.Failed:
                if (_activeEntryId is { } activeEntryId && TryFindEntry(activeEntryId, out _, out var failedEntry) && failedEntry != null)
                    SkipEntryForCurrentRun(failedEntry, $"{result.Message} Skipping it for the current run.", false);
                _activeEntryId = null;

                if (!VendorInteractionHelper.IsReadyToLeaveVendor())
                {
                    switch (TryContinueWithCurrentVendor(new VendorExecutionGroup(result.Vendor.NpcId, result.Vendor.MenuShopType, result.Vendor.ShopId)))
                    {
                        case SameVendorContinuationResult.Started:
                            return;
                        case SameVendorContinuationResult.Failed:
                            BeginShopCloseTransition(_statusText);
                            return;
                    }
                }

                BeginShopCloseTransition($"Skipping {result.ItemName} and continuing the vendor list.");
                break;
        }
    }

    private void BeginShopCloseTransition(string statusText)
    {
        _statusText = statusText;
        if (VendorInteractionHelper.IsReadyToLeaveVendor())
        {
            ResetShopCloseWaitState();
            if (_isRunning)
                TryStartNextEntry();
            return;
        }

        _waitingForShopClose       = true;
        _shopCloseStartTime        = DateTime.UtcNow;
        _lastShopCloseAttemptTime  = DateTime.MinValue;
        _lastShopCloseBlockerLogTime = DateTime.MinValue;
        if (VendorInteractionHelper.TryExitVendorInteraction())
            _lastShopCloseAttemptTime = DateTime.UtcNow;
    }

    private void ResetShopCloseWaitState()
    {
        _waitingForShopClose        = false;
        _shopCloseStartTime         = DateTime.MinValue;
        _lastShopCloseAttemptTime   = DateTime.MinValue;
        _lastShopCloseBlockerLogTime = DateTime.MinValue;
    }

    private static void EnsureVendorCachesAvailable()
    {
        VendorShopResolver.InitializeAsync();
        if (!VendorShopResolver.IsInitialized)
            return;
        VendorNpcLocationCache.InitializeAsync(VendorShopResolver.AllVendorNpcIds);
    }

    private static string DescribeShopType(VendorShopType shopType)
        => shopType switch
        {
            VendorShopType.GilShop           => "gil-shop",
            VendorShopType.SpecialCurrency   => "special-currency",
            VendorShopType.GrandCompanySeals => "grand-company",
            _                                => "vendor",
        };
}
