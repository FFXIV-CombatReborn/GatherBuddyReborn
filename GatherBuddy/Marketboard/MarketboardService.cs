using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Marketboard;

public sealed class MarketboardService : IDisposable
{
    private const int CacheExpiryMinutes = 15;
    private const int MaxHistoryItems    = 100;
    private const string HistoryFile     = "mb_history.json";

    private sealed record PersistedEntry(uint ItemId, string Name, uint IconId);

    private readonly object              _lock        = new();
    private readonly UniversalisService  _universalis = new();

    public MarketboardService() => LoadHistory();
    private readonly Dictionary<(uint, string), (MarketItemData Data, DateTime FetchedAt)> _cache    = new();
    private readonly Dictionary<uint, string>                                              _names    = new();
    private readonly Dictionary<uint, uint>                                                _icons    = new();
    private readonly List<uint>                                                            _history  = new();
    private readonly HashSet<(uint, string)>                                               _pending  = new();
    private readonly HashSet<(uint, string)>                                               _errors   = new();
    private          CancellationTokenSource                                               _cts      = new();

    public bool IsPending(uint itemId, string scope) { lock (_lock) return _pending.Contains((itemId, scope)); }
    public bool HasError(uint itemId,  string scope) { lock (_lock) return _errors.Contains((itemId, scope));  }

    public MarketItemData? GetCached(uint itemId, string scope)
    {
        lock (_lock)
            return _cache.TryGetValue((itemId, scope), out var e) ? e.Data : null;
    }

    public DateTime GetFetchTime(uint itemId, string scope)
    {
        lock (_lock)
            return _cache.TryGetValue((itemId, scope), out var e) ? e.FetchedAt : DateTime.MinValue;
    }

    public string GetItemName(uint itemId)
    {
        lock (_lock)
            return _names.TryGetValue(itemId, out var n) ? n : $"Item #{itemId}";
    }

    public uint GetItemIcon(uint itemId)
    {
        lock (_lock)
            return _icons.TryGetValue(itemId, out var icon) ? icon : 0;
    }

    public List<uint> GetHistorySnapshot()
    {
        lock (_lock) return new List<uint>(_history);
    }

    public void QueueLookup(uint itemId, string itemName, uint iconId = 0)
        => QueueLookup(itemId, itemName, iconId, GetDataCenter());

    public void QueueLookup(uint itemId, string itemName, uint iconId, string scope)
    {
        lock (_lock)
        {
            _names[itemId] = itemName;
            if (iconId > 0) _icons[itemId] = iconId;

            if (_cache.TryGetValue((itemId, scope), out var cached) &&
                (DateTime.UtcNow - cached.FetchedAt).TotalMinutes < CacheExpiryMinutes)
            {
                MoveToFront(itemId);
                return;
            }

            if (_pending.Contains((itemId, scope))) return;

            _pending.Add((itemId, scope));
            _errors.Remove((itemId, scope));
            MoveToFront(itemId);
        }

        var canBeHq = false;
        try
        {
            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            if (itemSheet?.TryGetRow(itemId, out var lumItem) == true)
                canBeHq = lumItem.CanBeHq;
        }
        catch { }

        var ct = _cts.Token;
        Task.Run(async () =>
        {
            try
            {
                List<MarketItemData> results;
                if (canBeHq)
                {
                    var nqRes = await _universalis.GetMarketDataAsync(scope, new[] { itemId }, 10, ct, false);
                    await Task.Delay(300, ct);
                    var hqRes = await _universalis.GetMarketDataAsync(scope, new[] { itemId }, 10, ct, true);
                    var nqData = nqRes.FirstOrDefault(r => r.ItemId == itemId);
                    var hqData = hqRes.FirstOrDefault(r => r.ItemId == itemId);
                    var base_ = nqData ?? hqData;
                    if (base_ != null)
                    {
                        var merged = new MarketItemData
                        {
                            ItemId   = base_.ItemId,
                            MinPrice = base_.MinPrice,
                            Listings = (nqData?.Listings ?? new())
                                       .Concat(hqData?.Listings ?? new())
                                       .ToList(),
                        };
                        results = new List<MarketItemData> { merged };
                    }
                    else
                    {
                        results = new List<MarketItemData>();
                    }
                }
                else
                {
                    results = await _universalis.GetMarketDataAsync(scope, new[] { itemId }, 20, ct);
                }
                var data = results.FirstOrDefault(r => r.ItemId == itemId);

                lock (_lock)
                {
                    _pending.Remove((itemId, scope));
                    if (data != null)
                    {
                        data.ItemName = itemName;
                        if (_icons.TryGetValue(itemId, out var icon)) data.IconId = icon;
                        _cache[(itemId, scope)] = (data, DateTime.UtcNow);
                    }
                    else
                    {
                        _errors.Add((itemId, scope));
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _pending.Remove((itemId, scope));
                    _errors.Add((itemId, scope));
                }
                GatherBuddy.Log.Warning($"[Marketboard] Lookup failed for {itemName} ({itemId}): {ex.Message}");
            }
        }, ct);
    }

    public void ForceRefresh(uint itemId, string scope)
    {
        lock (_lock) _cache.Remove((itemId, scope));
        var name = GetItemName(itemId);
        var icon = GetItemIcon(itemId);
        QueueLookup(itemId, name, icon, scope);
    }

    public void RefreshAll()
    {
        var dc = GetDataCenter();
        List<uint>   ids;
        List<string> names;
        List<uint>   icons;

        lock (_lock)
        {
            ids   = new List<uint>(_history);
            names = ids.Select(id => _names.TryGetValue(id, out var n) ? n : string.Empty).ToList();
            icons = ids.Select(id => _icons.TryGetValue(id, out var ic) ? ic : 0u).ToList();
            foreach (var id in ids) _cache.Remove((id, dc));
        }

        for (var i = 0; i < ids.Count; i++)
            QueueLookup(ids[i], names[i], icons[i], dc);
    }

    public void RemoveFromHistory(uint itemId)
    {
        lock (_lock)
        {
            _history.Remove(itemId);
            _names.Remove(itemId);
            _icons.Remove(itemId);
            var cacheKeys = _cache.Keys.Where(k => k.Item1 == itemId).ToList();
            foreach (var k in cacheKeys) _cache.Remove(k);
            var errKeys = _errors.Where(k => k.Item1 == itemId).ToList();
            foreach (var k in errKeys) _errors.Remove(k);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
            _history.Clear();
            _errors.Clear();
        }
    }

    public List<string> GetOtherDcs()
    {
        var result = new List<string>();
        var homeDc = GetDataCenter();
        try
        {
            var dcSheet = Dalamud.GameData.GetExcelSheet<WorldDCGroupType>();
            if (dcSheet == null) return result;
            foreach (var dc in dcSheet)
            {
                if (dc.IsCloud) continue;
                if (dc.Region.RowId < 1 || dc.Region.RowId > 4) continue;
                var name = dc.Name.ExtractText();
                if (!string.IsNullOrEmpty(name) && name != homeDc)
                    result.Add(name);
            }
            result.Sort();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] Other DCs query failed: {ex.Message}");
        }
        return result;
    }

    public List<string> GetDcWorlds()
    {
        var result = new List<string>();
        try
        {
            var worldId = Dalamud.Objects.LocalPlayer?.HomeWorld.RowId ?? 0u;
            if (worldId == 0) return result;

            var worldSheet = Dalamud.GameData.GetExcelSheet<World>();
            if (worldSheet == null) return result;
            if (!worldSheet.TryGetRow(worldId, out var homeWorld)) return result;

            var dcId = homeWorld.DataCenter.RowId;
            foreach (var world in worldSheet)
            {
                if (world.DataCenter.RowId == dcId && world.IsPublic)
                {
                    var name = world.Name.ExtractText();
                    if (!string.IsNullOrEmpty(name))
                        result.Add(name);
                }
            }
            result.Sort();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] DC world list failed: {ex.Message}");
        }
        return result;
    }

    public string GetDataCenter()
    {
        try
        {
            var worldId = Dalamud.Objects.LocalPlayer?.HomeWorld.RowId ?? 0u;
            if (worldId == 0) return "Aether";
            var worldSheet = Dalamud.GameData.GetExcelSheet<World>();
            if (worldSheet?.TryGetRow(worldId, out var world) == true)
            {
                var dc = world.DataCenter.ValueNullable?.Name.ExtractText();
                if (!string.IsNullOrEmpty(dc)) return dc;
            }
            return "Aether";
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] DC resolution failed: {ex.Message}");
            return "Aether";
        }
    }

    private void MoveToFront(uint itemId)
    {
        _history.Remove(itemId);
        _history.Insert(0, itemId);
        if (_history.Count > MaxHistoryItems)
            _history.RemoveAt(_history.Count - 1);
    }

    private void LoadHistory()
    {
        try
        {
            var path = Path.Combine(Dalamud.PluginInterface.GetPluginConfigDirectory(), HistoryFile);
            if (!File.Exists(path)) return;
            var entries = JsonSerializer.Deserialize<List<PersistedEntry>>(File.ReadAllText(path));
            if (entries == null) return;
            lock (_lock)
            {
                foreach (var e in entries)
                {
                    if (e.ItemId == 0) continue;
                    _names[e.ItemId] = e.Name;
                    if (e.IconId > 0) _icons[e.ItemId] = e.IconId;
                    if (!_history.Contains(e.ItemId)) _history.Add(e.ItemId);
                }
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] Failed to load history: {ex.Message}");
        }
    }

    private void SaveHistory()
    {
        try
        {
            List<PersistedEntry> entries;
            lock (_lock)
            {
                entries = _history
                    .Select(id => new PersistedEntry(
                        id,
                        _names.TryGetValue(id, out var n) ? n : string.Empty,
                        _icons.TryGetValue(id, out var ic) ? ic : 0u))
                    .ToList();
            }
            var path = Path.Combine(Dalamud.PluginInterface.GetPluginConfigDirectory(), HistoryFile);
            File.WriteAllText(path, JsonSerializer.Serialize(entries));
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] Failed to save history: {ex.Message}");
        }
    }

    public void Dispose()
    {
        SaveHistory();
        _cts.Cancel();
        _cts.Dispose();
        _universalis.Dispose();
    }
}
