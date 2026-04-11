using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using GatherBuddy.Alarms;
using GatherBuddy.Classes;
using GatherBuddy.GatherGroup;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using Newtonsoft.Json;

namespace GatherBuddy.AutoGather.Lists;

public class AutoGatherList
{
    public sealed class Entry
    {
        public required IGatherable Item { get; set; }
        public uint Quantity { get; set; }
        public ILocation? PreferredLocation { get; set; }
        public bool Enabled { get; set; } = true;
    }

    public ReadOnlyCollection<Entry> Entries
        => entries.AsReadOnly();

    public ReadOnlyCollection<IGatherable> Items
        => entries.Select(e => e.Item).ToList().AsReadOnly();

    public ReadOnlyDictionary<IGatherable, uint> Quantities
        => entries.GroupBy(e => e.Item).ToDictionary(g => g.Key, g => g.First().Quantity).AsReadOnly();

    public ReadOnlyDictionary<IGatherable, ILocation> PreferredLocations
        => entries.Where(e => e.PreferredLocation != null)
            .GroupBy(e => e.Item)
            .ToDictionary(g => g.Key, g => g.First().PreferredLocation!)
            .AsReadOnly();

    public ReadOnlyDictionary<IGatherable, bool> EnabledItems
        => entries.GroupBy(e => e.Item).ToDictionary(g => g.Key, g => g.First().Enabled).AsReadOnly();

    public string Name        { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FolderPath  { get; set; } = string.Empty;
    public int    Order       { get; set; } = 0;
    public bool   Enabled     { get; set; } = false;
    public bool   Fallback    { get; set; } = false;

    private List<Entry> entries = [];

    public AutoGatherList Clone()
        => new()
        {
            entries = entries.Select(e => new Entry
            {
                Item = e.Item,
                Quantity = e.Quantity,
                PreferredLocation = e.PreferredLocation,
                Enabled = e.Enabled,
            }).ToList(),
            Name        = Name,
            Description = Description,
            FolderPath  = FolderPath,
            Order       = Order,
            Enabled     = false,
            Fallback    = Fallback,
        };

    public bool Add(IGatherable item, uint quantity = 1, ILocation? preferredLocation = null)
    {
        if (entries.Any(e => e.Item == item && e.PreferredLocation == preferredLocation))
            return false;

        entries.Add(new Entry
        {
            Item = item,
            Quantity = NormalizeQuantity(item, quantity),
            PreferredLocation = preferredLocation,
            Enabled = true,
        });
        return true;
    }

    public void RemoveAt(int index)
        => entries.RemoveAt(index);

    public bool Replace(int index, IGatherable item)
    {
        var entry = entries[index];
        if (entries.Where((_, i) => i != index).Any(e => e.Item == item && e.PreferredLocation == entry.PreferredLocation))
            return false;

        entry.Item = item;
        entry.Quantity = NormalizeQuantity(item, entry.Quantity);
        if (entry.PreferredLocation != null && !item.Locations.Contains(entry.PreferredLocation))
            entry.PreferredLocation = null;

        return true;
    }

    public bool Move(int from, int to)
        => Functions.Move(entries, from, to);

    public bool SetQuantity(int index, uint quantity)
    {
        if (index < 0 || index >= entries.Count)
            return false;

        var entry = entries[index];
        quantity = NormalizeQuantity(entry.Item, quantity);
        if (entry.Quantity == quantity)
            return false;

        entry.Quantity = quantity;
        return true;
    }

    public bool SetEnabled(int index, bool enabled)
    {
        if (index < 0 || index >= entries.Count)
            return false;

        var entry = entries[index];
        if (entry.Enabled == enabled)
            return false;

        entry.Enabled = enabled;
        return true;
    }

    private static uint NormalizeQuantity(IGatherable item, uint quantity)
    {
        if (quantity < 1)
            quantity = 1;
        if (quantity > 999999)
            quantity = 999999;
        return quantity;
    }

    public bool SetPreferredLocation(int index, ILocation? location)
    {
        if (index < 0 || index >= entries.Count)
            return false;

        var entry = entries[index];
        if (entry.PreferredLocation == location)
            return false;

        if (entries.Where((_, i) => i != index).Any(e => e.Item == entry.Item && e.PreferredLocation == location))
            return false;

        entry.PreferredLocation = location;
        return true;
    }

    public bool HasItems()
        => Enabled && entries.Count > 0;

    public struct Config(AutoGatherList list)
    {
        public const byte CurrentVersion = 6;

        public struct EntryConfig(Entry entry)
        {
            public uint ItemId = entry.Item.ItemId;
            public uint Quantity = entry.Quantity;
            public uint? PreferredLocationId = entry.PreferredLocation?.Id;
            public bool Enabled = entry.Enabled;
        }

        public EntryConfig[]          Entries            = list.entries.Select(e => new EntryConfig(e)).ToArray();
        public uint[]                 ItemIds            = [];
        public Dictionary<uint, uint> Quantities         = [];
        public Dictionary<uint, uint> PrefferedLocations = [];
        public Dictionary<uint, bool> EnabledItems       = [];
        public string                 Name               = list.Name;
        public string                 Description        = list.Description;
        public string                 FolderPath         = list.FolderPath;
        public int                    Order              = list.Order;
        public bool                   Enabled            = list.Enabled;
        public bool                   Fallback           = list.Fallback;

        internal readonly string ToBase64()
        {
            var json  = JsonConvert.SerializeObject(this);
            var bytes = Encoding.UTF8.GetBytes(json).Prepend(CurrentVersion).ToArray();
            return Functions.CompressedBase64(bytes);
        }

        [SuppressMessage("ReSharper", "ConditionIsAlwaysTrueOrFalse")]
        internal static bool FromBase64(string data, out Config cfg)
        {
            cfg = default;
            try
            {
                var bytes = Functions.DecompressedBase64(data);
                if (bytes.Length == 0 || (bytes[0] != CurrentVersion && bytes[0] != 5 && bytes[0] != 4))
                    return false;

                var json = Encoding.UTF8.GetString(bytes.AsSpan()[1..]);
                cfg = JsonConvert.DeserializeObject<Config>(json);
                if (cfg.ItemIds == null
                 || cfg.Entries == null
                 || cfg.Name == null
                 || cfg.Description == null
                 || cfg.Quantities == null
                 || cfg.PrefferedLocations == null)
                    return false;

                cfg.FolderPath ??= string.Empty;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static bool FromConfig(Config cfg, out AutoGatherList list)
    {
        if (cfg.EnabledItems == null || cfg.EnabledItems.Count == 0)
        {
            cfg.EnabledItems = new(cfg.ItemIds.Length);
            foreach (var item in cfg.ItemIds)
                cfg.EnabledItems[item] = true;
        }

        list = new AutoGatherList
        {
            Name        = cfg.Name,
            Description = cfg.Description,
            FolderPath  = cfg.FolderPath ?? string.Empty,
            Order       = cfg.Order,
            Enabled     = cfg.Enabled,
            Fallback    = cfg.Fallback,
        };

        var changes = false;

        if (cfg.Entries.Length > 0)
        {
            foreach (var entryCfg in cfg.Entries)
            {
                if (!TryResolveItem(entryCfg.ItemId, out var item))
                    continue;

                ILocation? location = null;
                if (entryCfg.PreferredLocationId is { } locId)
                {
                    location = item.Locations.FirstOrDefault(n => n.Id == locId);
                    if (location == null)
                        changes = true;
                }

                list.entries.Add(new Entry
                {
                    Item = item,
                    Quantity = NormalizeQuantity(item, entryCfg.Quantity),
                    PreferredLocation = location,
                    Enabled = entryCfg.Enabled,
                });
            }

            return changes;
        }

        foreach (var itemId in cfg.ItemIds)
        {
            if (!TryResolveItem(itemId, out var item))
                continue;

            var quantity = cfg.Quantities.GetValueOrDefault(item.ItemId);
            if (!list.Add(item, quantity))
                continue;

            var entry = list.entries[^1];
            changes |= entry.Quantity != quantity;
            if (cfg.PrefferedLocations.TryGetValue(itemId, out var locId))
            {
                if (item.Locations.FirstOrDefault(n => n.Id == locId) is var loc and not null)
                    entry.PreferredLocation = loc;
                else
                    changes = true;
            }

            if (cfg.EnabledItems.TryGetValue(itemId, out var enabled))
                entry.Enabled = enabled;
        }

        return changes;
    }

    private static bool TryResolveItem(uint itemId, [NotNullWhen(true)] out IGatherable? item)
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

        item = null;
        return false;
    }

    public AutoGatherList()
    { }

    public AutoGatherList(TimedGroup group)
    {
        Name        = group.Name;
        Description = group.Description;
        foreach (var item in group.Nodes.Select(n => n.Item).OfType<Gatherable>())
            Add(item);
    }

    public AutoGatherList(AlarmGroup group)
    {
        Name        = group.Name;
        Description = group.Description;
        foreach (var item in group.Alarms.Select(n => n.Item).OfType<Gatherable>())
            Add(item);
    }
}
