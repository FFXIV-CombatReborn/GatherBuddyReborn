using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Interfaces;

namespace GatherBuddy.Plugin;

public sealed class GatherBuddyIpc : IDisposable
{
    public const int IpcVersion = 3;

    private readonly GatherBuddy _plugin;

    private readonly Dictionary<string, AutoGatherList> _trackedLists = new();
    private readonly Dictionary<AutoGatherList, string> _trackedListIds = new();
    private readonly Dictionary<string, List<AutoGatherList>> _sessionSuppressed = new();

    public GatherBuddyIpc(GatherBuddy plugin)
    {
        _plugin = plugin;
        EzIPC.Init(this, GatherBuddy.InternalName);
        Debug.Assert(AutoGatherWaiting != null);
        Debug.Assert(AutoGatherEnabledChanged != null);
    }

#pragma warning disable CA1822 // Mark members as static
    [EzIPC]
    public int Version()
        => IpcVersion;

    [EzIPC]
    public uint Identify(string text)
        => _plugin.Executor.Identificator.IdentifyGatherable(text)?.ItemId
         ?? _plugin.Executor.Identificator.IdentifyFish(text)?.ItemId ?? 0;

    [EzIPC]
    public bool IsAutoGatherEnabled()
        => GatherBuddy.AutoGather.Enabled;

    [EzIPC]
    public string GetAutoGatherStatusText()
        => GatherBuddy.AutoGather.AutoStatus;

    [EzIPC]
    public void SetAutoGatherEnabled(bool enabled)
        => GatherBuddy.AutoGather.Enabled = enabled;

    [EzIPC]
    public bool IsAutoGatherWaiting()
        => GatherBuddy.AutoGather.Waiting;

    [EzIPCEvent]
    public Action AutoGatherWaiting;

    [EzIPCEvent]
    public Action<bool> AutoGatherEnabledChanged;

    // --- v3: AutoGather list management ---------------------------------

    [EzIPC]
    public bool IsItemGatherable(uint itemId)
        => LookupGatherable(itemId) != null;

    [EzIPC]
    public string[] GetAutoGatherListIds()
    {
        SyncTrackedLists();
        return _trackedListIds.Values.ToArray();
    }

    [EzIPC]
    public string GetAutoGatherListName(string listId)
        => TryGetList(listId, out var list) ? list!.Name : string.Empty;

    [EzIPC]
    public bool IsAutoGatherListEnabled(string listId)
        => TryGetList(listId, out var list) && list!.Enabled;

    [EzIPC]
    public bool IsAutoGatherListFallback(string listId)
        => TryGetList(listId, out var list) && list!.Fallback;

    [EzIPC]
    public bool SetAutoGatherListEnabled(string listId, bool enabled)
    {
        if (!TryGetList(listId, out var list) || list!.Enabled == enabled)
            return false;
        list.Enabled = enabled;
        _plugin.AutoGatherListsManager.Save();
        if (list.Items.Count > 0)
            _plugin.AutoGatherListsManager.SetActiveItems();
        return true;
    }

    [EzIPC]
    public string CreateAutoGatherList(string name, string description)
    {
        var list = new AutoGatherList
        {
            Name = string.IsNullOrWhiteSpace(name) ? "IPC List" : name,
            Description = description ?? string.Empty,
            Enabled = false,
        };
        _plugin.AutoGatherListsManager.AddList(list);
        return TrackList(list);
    }

    [EzIPC]
    public bool DeleteAutoGatherList(string listId)
    {
        if (!TryGetList(listId, out var list))
            return false;
        _plugin.AutoGatherListsManager.DeleteList(list!);
        UntrackList(list!);
        return true;
    }

    [EzIPC]
    public bool AutoGatherListAddItem(string listId, uint itemId, uint quantity)
    {
        if (!TryGetList(listId, out var list))
            return false;
        var item = LookupGatherable(itemId);
        if (item == null)
            return false;
        if (!list!.Add(item, Math.Max(1u, quantity)))
            return false;
        _plugin.AutoGatherListsManager.Save();
        if (list.Enabled)
            _plugin.AutoGatherListsManager.SetActiveItems();
        return true;
    }

    [EzIPC]
    public bool AutoGatherListClear(string listId)
    {
        if (!TryGetList(listId, out var list))
            return false;
        var changed = false;
        for (var i = list!.Items.Count - 1; i >= 0; i--)
        {
            list.RemoveAt(i);
            changed = true;
        }
        if (!changed)
            return false;
        _plugin.AutoGatherListsManager.Save();
        if (list.Enabled)
            _plugin.AutoGatherListsManager.SetActiveItems();
        return true;
    }

    [EzIPC]
    public string BeginAutoGatherExclusiveSession(string ownerLabel, Dictionary<uint, uint> targets)
    {
        if (targets == null || targets.Count == 0)
            return string.Empty;

        var label = string.IsNullOrWhiteSpace(ownerLabel) ? "IPC Caller" : ownerLabel;
        var list = new AutoGatherList
        {
            Name = $"{label} Session",
            Description = $"Temporary list managed by {label} via IPC.",
            Enabled = true,
        };

        var added = 0;
        foreach (var (itemId, qty) in targets)
        {
            var item = LookupGatherable(itemId);
            if (item == null)
                continue;
            if (list.Add(item, Math.Max(1u, qty)))
                added++;
        }

        if (added == 0)
            return string.Empty;

        var suppressed = SuppressOtherLists(list);
        _plugin.AutoGatherListsManager.AddList(list);
        var sessionId = TrackList(list);
        _sessionSuppressed[sessionId] = suppressed;
        GatherBuddy.AutoGather.Enabled = true;
        return sessionId;
    }

    [EzIPC]
    public bool EndAutoGatherExclusiveSession(string sessionId)
    {
        if (!TryGetList(sessionId, out var list))
            return false;

        GatherBuddy.AutoGather.Enabled = false;
        _plugin.AutoGatherListsManager.DeleteList(list!);
        UntrackList(list!);

        if (_sessionSuppressed.Remove(sessionId, out var suppressed))
            RestoreSuppressedLists(suppressed);

        return true;
    }

#pragma warning restore CA1822 // Mark members as static

    public void Dispose()
    {
        foreach (var sessionId in _sessionSuppressed.Keys.ToArray())
            EndAutoGatherExclusiveSession(sessionId);
        // EzIPC disposal is handled in GatherBuddy.cs Dispose method
    }

    private IGatherable? LookupGatherable(uint itemId)
    {
        if (GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var gatherable))
            return gatherable;
        if (GatherBuddy.GameData.Fishes.TryGetValue(itemId, out var fish))
            return fish;
        return null;
    }

    private bool TryGetList(string? listId, out AutoGatherList? list)
    {
        list = null;
        if (string.IsNullOrEmpty(listId))
            return false;
        if (!_trackedLists.TryGetValue(listId, out var cached))
            return false;

        // Verify list still exists in the manager; otherwise drop the stale entry.
        var stillPresent = _plugin.AutoGatherListsManager.Lists.Any(l => ReferenceEquals(l, cached));
        if (!stillPresent)
        {
            UntrackList(cached);
            return false;
        }
        list = cached;
        return true;
    }

    private string TrackList(AutoGatherList list)
    {
        if (_trackedListIds.TryGetValue(list, out var existing))
            return existing;

        var id = Guid.NewGuid().ToString("N");
        _trackedLists[id] = list;
        _trackedListIds[list] = id;
        return id;
    }

    private void UntrackList(AutoGatherList list)
    {
        if (_trackedListIds.Remove(list, out var id))
            _trackedLists.Remove(id);
    }

    private void SyncTrackedLists()
    {
        var currentLists = _plugin.AutoGatherListsManager.Lists.ToList();
        var currentSet = new HashSet<AutoGatherList>(currentLists, ReferenceEqualityComparer.Instance);

        foreach (var (list, _) in _trackedListIds.ToArray())
        {
            if (!currentSet.Contains(list))
                UntrackList(list);
        }

        foreach (var list in currentLists)
        {
            if (!_trackedListIds.ContainsKey(list))
                TrackList(list);
        }
    }

    private List<AutoGatherList> SuppressOtherLists(AutoGatherList exclude)
    {
        var suppressed = new List<AutoGatherList>();
        foreach (var list in _plugin.AutoGatherListsManager.Lists)
        {
            if (ReferenceEquals(list, exclude))
                continue;
            if (!list.Enabled || list.Fallback)
                continue;
            list.Enabled = false;
            suppressed.Add(list);
        }
        return suppressed;
    }

    private void RestoreSuppressedLists(List<AutoGatherList> suppressed)
    {
        if (suppressed.Count == 0)
            return;
        foreach (var list in suppressed)
            list.Enabled = true;
        _plugin.AutoGatherListsManager.SetActiveItems();
        _plugin.AutoGatherListsManager.Save();
    }
}
