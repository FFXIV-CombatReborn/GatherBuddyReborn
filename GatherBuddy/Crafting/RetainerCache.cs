using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Plugin;

namespace GatherBuddy.Crafting;

public static class RetainerCache
{
    private static Dictionary<uint, (uint NQ, uint HQ)> _cache = new();
    private static bool _isDirty = false;
    private static readonly object _lockObj = new();

    private static ICallGateSubscriber<(uint, InventoryItem.ItemFlags, ulong, uint), bool>? _onItemAdded;
    private static ICallGateSubscriber<(uint, InventoryItem.ItemFlags, ulong, uint), bool>? _onItemRemoved;

    public static void Initialize()
    {
        try
        {
            _onItemAdded = Dalamud.PluginInterface.GetIpcSubscriber<(uint, InventoryItem.ItemFlags, ulong, uint), bool>("AllaganTools.ItemAdded");
            _onItemRemoved = Dalamud.PluginInterface.GetIpcSubscriber<(uint, InventoryItem.ItemFlags, ulong, uint), bool>("AllaganTools.ItemRemoved");

            _onItemAdded.Subscribe(OnItemAddedHandler);
            _onItemRemoved.Subscribe(OnItemRemovedHandler);
            
            GatherBuddy.Log.Debug("[RetainerCache] Initialized Allagan Tools IPC subscriptions");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[RetainerCache] Failed to subscribe to Allagan Tools IPC events: {ex.Message}");
        }
    }

    public static void Dispose()
    {
        _onItemAdded?.Unsubscribe(OnItemAddedHandler);
        _onItemRemoved?.Unsubscribe(OnItemRemovedHandler);
        GatherBuddy.Log.Debug("[RetainerCache] Disposed IPC subscriptions");
    }

    public static void ClearCache()
    {
        lock (_lockObj)
        {
            _cache.Clear();
            _isDirty = false;
        }
        GatherBuddy.Log.Debug("[RetainerCache] Cache cleared");
    }

    public static void OnLogout()
    {
        ClearCache();
    }

    private static void OnItemAddedHandler((uint itemId, InventoryItem.ItemFlags flags, ulong retainerId, uint page) args)
    {
        GatherBuddy.Log.Debug($"[RetainerCache] ItemAdded: Item={args.itemId}, Retainer={args.retainerId}, Page={args.page}");
        MarkDirty();
    }

    private static void OnItemRemovedHandler((uint itemId, InventoryItem.ItemFlags flags, ulong retainerId, uint page) args)
    {
        GatherBuddy.Log.Debug($"[RetainerCache] ItemRemoved: Item={args.itemId}, Retainer={args.retainerId}, Page={args.page}");
        MarkDirty();
    }

    private static void MarkDirty()
    {
        lock (_lockObj)
        {
            _cache.Clear();
            _isDirty = true;
        }
        GatherBuddy.Log.Debug("[RetainerCache] Cache invalidated by inventory change");
    }

    public static uint GetRetainerItemCount(uint itemId)
    {
        if (!AllaganTools.Enabled || AllaganTools.ItemCount == null)
            return 0;

        lock (_lockObj)
        {
            if (_isDirty)
            {
                _isDirty = false;
            }

            if (_cache.TryGetValue(itemId, out var counts))
            {
                return counts.NQ + counts.HQ;
            }

            var total = QueryAllRetainers(itemId, false);
            _cache[itemId] = total;
            return total.NQ + total.HQ;
        }
    }

    public static uint GetRetainerItemCountHQ(uint itemId)
    {
        if (!AllaganTools.Enabled || AllaganTools.ItemCountHQ == null)
            return 0;

        lock (_lockObj)
        {
            if (_isDirty)
            {
                _isDirty = false;
            }

            if (_cache.TryGetValue(itemId, out var counts))
            {
                return counts.HQ;
            }

            var total = QueryAllRetainers(itemId, true);
            _cache[itemId] = total;
            return total.HQ;
        }
    }

    private static unsafe (uint NQ, uint HQ) QueryAllRetainers(uint itemId, bool hqOnly)
    {
        try
        {
            uint totalNQ = 0;
            uint totalHQ = 0;

            var retainerMgr = RetainerManager.Instance();
            if (retainerMgr == null)
                return (0, 0);

            var retainerCount = retainerMgr->GetRetainerCount();

            for (uint i = 0; i < retainerCount; i++)
            {
                var retainer = retainerMgr->GetRetainerBySortedIndex(i);
                if (retainer == null || retainer->RetainerId == 0)
                    continue;

                var retainerId = retainer->RetainerId;

                for (uint page = 10000; page <= 10006; page++)
                {
                    if (!hqOnly)
                        totalNQ += AllaganTools.ItemCount(itemId, retainerId, page);
                    totalHQ += AllaganTools.ItemCountHQ(itemId, retainerId, page);
                }

                if (!hqOnly)
                    totalNQ += AllaganTools.ItemCount(itemId, retainerId, 12001);
                totalHQ += AllaganTools.ItemCountHQ(itemId, retainerId, 12001);
            }

            return (totalNQ, totalHQ);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[RetainerCache] Error querying retainers for item {itemId}: {ex.Message}");
            return (0, 0);
        }
    }
}
