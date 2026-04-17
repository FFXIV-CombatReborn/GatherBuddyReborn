using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.Plugin;

namespace GatherBuddy.Vulcan.Vendors;

public sealed class VendorBuyListManager : IDisposable
{
    private readonly record struct VendorExecutionGroup(uint NpcId, VendorMenuShopType MenuShopType, uint ShopId);
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
        _activeEntryId = null;
        _isRunning     = false;
        _runningListId = null;
        _currentExecutionVendor = null;
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

        var existing = FindMatchingEntry(list, liveEntry, selectedVendor);
        if (existing != null)
        {
            var mergedTargetQuantity = Math.Max(existing.TargetQuantity, entry.TargetQuantity);
            UpdateEntry(existing, liveEntry, selectedVendor, mergedTargetQuantity);
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

        _isRunning = false;
        _runningListId = null;
        _currentExecutionVendor = null;
        _statusText = "Vendor list stopped.";
        var shouldStopPurchase = _activeEntryId.HasValue && GatherBuddy.VendorPurchaseManager.IsRunning;
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

        var candidates = GetCandidateEntries(entry)
            .Where(candidate => candidate.ItemId == entry.ItemId
                && candidate.CurrencyItemId == entry.CurrencyItemId
                && candidate.Cost == entry.Cost)
            .ToList();
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

    private bool TryAddTarget(VendorBuyListDefinition list, VendorShopEntry entry, VendorNpc vendor, uint targetQuantity, bool selectList,
        bool openWindow, bool announce)
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

    private bool TryIncrementTarget(VendorBuyListDefinition list, uint itemId, uint amount, bool selectList, bool openWindow, bool announce)
    {
        amount = Math.Max(1u, amount);

        var existing = list.Entries
            .FirstOrDefault(entry => entry.ItemId == itemId && entry.ShopType == VendorShopType.GilShop);
        if (existing != null)
        {
            existing.TargetQuantity = SaturatingAdd(Math.Max(existing.TargetQuantity, (uint)Math.Max(0, GetCurrentInventoryAndArmoryCount(itemId))), amount);
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

    private static VendorBuyListEntry? FindMatchingEntry(VendorBuyListDefinition list, VendorShopEntry entry, VendorNpc vendor)
        => list.Entries.FirstOrDefault(existing =>
            existing.ShopType == entry.ShopType
         && existing.SourceShopId == vendor.SourceShopId
         && existing.ShopItemIndex == vendor.ShopItemIndex
         && existing.GcRankIndex == vendor.GcRankIndex
         && existing.GcCategoryIndex == vendor.GcCategoryIndex
         && existing.ItemId == entry.ItemId
         && existing.CurrencyItemId == entry.CurrencyItemId
         && existing.Cost == entry.Cost
         && existing.VendorNpcId == vendor.NpcId
         && existing.MenuShopType == vendor.MenuShopType
         && existing.ShopId == vendor.ShopId);

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

    private VendorBuyListEntry? GetNextPendingEntry(VendorBuyListDefinition list)
    {
        if (_currentExecutionVendor.HasValue)
        {
            var currentVendor = _currentExecutionVendor.Value;
            var groupedEntry = list.Entries.FirstOrDefault(entry =>
                entry.VendorNpcId == currentVendor.NpcId
             && entry.MenuShopType == currentVendor.MenuShopType
             && entry.ShopId == currentVendor.ShopId
             && GetRemainingQuantity(entry) > 0);
            if (groupedEntry != null)
                return groupedEntry;
        }

        return list.Entries.FirstOrDefault(entry => GetRemainingQuantity(entry) > 0);
    }

    private SameVendorContinuationResult TryContinueWithCurrentVendor(VendorExecutionGroup vendorGroup)
    {
        if (!_isRunning)
            return SameVendorContinuationResult.NoCompatibleEntry;

        var list = GetExecutionList();
        if (list == null)
        {
            _isRunning = false;
            _activeEntryId = null;
            _runningListId = null;
            _currentExecutionVendor = null;
            _statusText = "The active vendor list is no longer available.";
            return SameVendorContinuationResult.Failed;
        }

        foreach (var entry in list.Entries)
        {
            if (entry.VendorNpcId != vendorGroup.NpcId || entry.MenuShopType != vendorGroup.MenuShopType || entry.ShopId != vendorGroup.ShopId)
                continue;

            var remainingQuantity = GetRemainingQuantity(entry);
            if (remainingQuantity == 0)
                continue;

            if (!TryResolvePurchaseContext(entry, out var liveEntry, out var vendor, out var location, out var errorMessage))
            {
                _isRunning = false;
                _activeEntryId = null;
                _runningListId = null;
                _currentExecutionVendor = null;
                _statusText = errorMessage;
                return SameVendorContinuationResult.Failed;
            }

            var resolvedVendorGroup = new VendorExecutionGroup(vendor.NpcId, vendor.MenuShopType, vendor.ShopId);
            if (resolvedVendorGroup != vendorGroup)
            {
                GatherBuddy.Log.Debug($"[VendorBuyListManager] {entry.ItemName} no longer resolves to the current vendor batch {vendorGroup.NpcId}/{vendorGroup.MenuShopType}/{vendorGroup.ShopId}; closing the shop before switching vendors");
                return SameVendorContinuationResult.NoCompatibleEntry;
            }

            _activeEntryId = entry.Id;
            _statusText = $"Buying {remainingQuantity:N0}x {entry.ItemName} from {vendor.Name} in '{list.Name}'.";
            GatherBuddy.VendorPurchaseManager.StartPurchase(liveEntry, vendor, location, remainingQuantity, true);
            if (!GatherBuddy.VendorPurchaseManager.IsRunning)
            {
                _isRunning = false;
                _activeEntryId = null;
                _runningListId = null;
                _currentExecutionVendor = null;
                _statusText = $"Failed to start vendor purchase for {entry.ItemName}.";
                return SameVendorContinuationResult.Failed;
            }

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
            _isRunning = false;
            _activeEntryId = null;
            _runningListId = null;
            _currentExecutionVendor = null;
            _statusText = "The active vendor list is no longer available.";
            return;
        }

        var entry = GetNextPendingEntry(list);
        if (entry == null)
        {
            _isRunning = false;
            _activeEntryId = null;
            _runningListId = null;
            _currentExecutionVendor = null;
            _statusText = $"Vendor list '{list.Name}' complete.";
            GatherBuddy.Log.Information($"[VendorBuyListManager] Vendor list '{list.Name}' complete.");
            Communicator.Print($"[GatherBuddyReborn] Vendor list '{list.Name}' complete.");

            return;
        }
        var remainingQuantity = GetRemainingQuantity(entry);
        if (!TryResolvePurchaseContext(entry, out var liveEntry, out var vendor, out var location, out var errorMessage))
        {
            _isRunning = false;
            _activeEntryId = null;
            _runningListId = null;
            _currentExecutionVendor = null;
            _statusText = errorMessage;
            return;
        }

        var nextVendorGroup = new VendorExecutionGroup(vendor.NpcId, vendor.MenuShopType, vendor.ShopId);

        _currentExecutionVendor = nextVendorGroup;
        _activeEntryId = entry.Id;
        _statusText = $"Buying {remainingQuantity:N0}x {entry.ItemName} from {vendor.Name} in '{list.Name}'.";
        GatherBuddy.VendorPurchaseManager.StartPurchase(liveEntry, vendor, location, remainingQuantity);
        if (!GatherBuddy.VendorPurchaseManager.IsRunning)
        {
            _isRunning = false;
            _activeEntryId = null;
            _runningListId = null;
            _currentExecutionVendor = null;
            _statusText = $"Failed to start vendor purchase for {entry.ItemName}.";
        }
    }

    private bool TryResolvePurchaseContext(VendorBuyListEntry entry, out VendorShopEntry liveEntry, out VendorNpc vendor, out VendorNpcLocation location, out string errorMessage)
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
            return false;
        }
        if (!TryResolveLiveEntry(entry, out var resolvedEntry, out var resolvedVendor, out var resolvedLocation)
         || resolvedEntry == null
         || resolvedVendor == null)
        {
            liveEntry    = null!;
            vendor       = null!;
            location     = null!;
            errorMessage = $"Could not resolve {entry.ItemName} in the {DescribeShopType(entry.ShopType)} vendor data.";
            return false;
        }

        if (resolvedLocation == null)
        {
            liveEntry    = null!;
            vendor       = null!;
            location     = null!;
            errorMessage = $"No vendor location data is available for {resolvedVendor.Name}.";
            return false;
        }

        liveEntry = resolvedEntry;
        vendor    = resolvedVendor;
        location  = resolvedLocation;
        return true;
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
            case VendorPurchaseManager.CompletionState.Cancelled:
                _isRunning = false;
                _activeEntryId = null;
                _runningListId = null;
                _currentExecutionVendor = null;
                BeginShopCloseTransition("Leaving vendor interaction.");
                break;
            case VendorPurchaseManager.CompletionState.Failed:
                _isRunning = false;
                _activeEntryId = null;
                _runningListId = null;
                _currentExecutionVendor = null;
                BeginShopCloseTransition(result.Message);
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

        VendorNpcLocationCache.InitializeAsync(VendorShopResolver.GetAllVendorNpcIds());
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
