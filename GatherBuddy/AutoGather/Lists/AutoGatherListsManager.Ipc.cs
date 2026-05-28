using ElliLib.Filesystem;
using GatherBuddy.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.AutoGather.Lists;

public partial class AutoGatherListsManager
{
    public string[] GetIpcListPaths()
        => _fileSystem.Root.GetAllDescendants(SortMode)
            .OfType<FileSystem<AutoGatherList>.Leaf>()
            .Select(leaf => leaf.FullName())
            .ToArray();

    public string CreateIpcList(string name, string description)
    {
        var list = new AutoGatherList
        {
            Name = string.IsNullOrWhiteSpace(name) ? "IPC List" : name.Trim(),
            Description = description ?? string.Empty,
            Enabled = false,
        };

        AddList(list);
        return _fileSystem.TryGetValue(list, out var leaf) ? leaf.FullName() : string.Empty;
    }

    public int UpdateIpcList(string path, Dictionary<uint, uint>? targets, bool enabled, bool removeCompletedItems)
    {
        if (!TryGetIpcList(path, out var list))
            return 0;

        var changed = false;
        var refreshed = false;
        var validItems = new HashSet<IGatherable>();
        var validTargets = 0;

        foreach (var (itemId, quantity) in targets ?? [])
        {
            if (!TryResolveGatherable(itemId, out var item))
                continue;

            validItems.Add(item);
            validTargets++;

            var normalizedQuantity = Math.Max(1u, quantity);
            if (!list!.Quantities.ContainsKey(item))
            {
                changed |= list.Add(item, normalizedQuantity);
            }
            else
            {
                changed |= list.SetQuantity(item, normalizedQuantity);
            }

            changed |= list.SetEnabled(item, true);
        }

        foreach (var item in list!.Items)
            if (!validItems.Contains(item))
                changed |= list.SetEnabled(item, false);

        if (list.RemoveCompletedItems != removeCompletedItems)
        {
            list.RemoveCompletedItems = removeCompletedItems;
            changed = true;
        }

        var wasEnabled = list.Enabled;
        if (!ApplyIpcListEnabled(list, enabled))
            return 0;

        if (list.Enabled != wasEnabled)
        {
            changed = true;
            refreshed = true;
        }

        if (!changed)
            return validTargets;

        Save();
        if (refreshed || list.Items.Count > 0)
            SetActiveItems();

        return validTargets;
    }

    public bool SetIpcListEnabled(string path, bool enabled)
    {
        if (!TryGetIpcList(path, out var list))
            return false;

        var wasEnabled = list!.Enabled;
        if (!ApplyIpcListEnabled(list, enabled))
            return false;
        if (list.Enabled == wasEnabled)
            return true;

        Save();
        if (list.Items.Count > 0)
            SetActiveItems();

        return true;
    }

    private bool ApplyIpcListEnabled(AutoGatherList list, bool enabled)
    {
        if (list.Enabled == enabled)
            return true;
        if (enabled && (!ValidateFishingBait(list) || !ValidateGatherablePerception(list)))
            return false;

        list.Enabled = enabled;
        return true;
    }

    private bool TryGetIpcList(string? path, out AutoGatherList? list)
    {
        list = null;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (!_fileSystem.Find(path, out var found) || found is not FileSystem<AutoGatherList>.Leaf leaf)
            return false;

        list = leaf.Value;
        return true;
    }

    private static bool TryResolveGatherable(uint itemId, out IGatherable item)
    {
        if (GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var gatherable))
        {
            item = gatherable;
            return true;
        }

        if (GatherBuddy.GameData.Fishes.TryGetValue(itemId, out var fish))
        {
            item = fish;
            return true;
        }

        item = null!;
        return false;
    }
}
