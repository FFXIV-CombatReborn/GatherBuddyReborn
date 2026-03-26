using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GatherBuddy.Marketboard;

public sealed class UniversalisService : IDisposable
{
    private const string BaseUrl            = "https://universalis.app/api/v2";
    private const int    RequestTimeoutMs   = 15000;
    private const int    MaxItemsPerBatch   = 10;
    private const int    InterBatchDelayMs  = 500;
    private const int    MaxRetries         = 2;
    private const int    RetryDelayMs       = 2000;
    private const int    MaxResponseSizeBytes = 1024 * 1024;

    private readonly HttpClient      _http;
    private readonly SemaphoreSlim    _throttle = new(3, 3);

    public UniversalisService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(RequestTimeoutMs) };
        _http.DefaultRequestHeaders.Add("User-Agent", "GatherBuddyReborn-Vulcan");
    }

    public async Task<List<MarketItemData>> GetMarketDataAsync(
        string worldOrDc, IReadOnlyList<uint> itemIds, int listingCount = 20, CancellationToken ct = default, bool? hqFilter = null)
    {
        if (itemIds.Count == 0) return new();

        var results = new List<MarketItemData>();

        for (var i = 0; i < itemIds.Count; i += MaxItemsPerBatch)
        {
            ct.ThrowIfCancellationRequested();

            if (i > 0)
                await Task.Delay(InterBatchDelayMs, ct);

            var end = Math.Min(i + MaxItemsPerBatch, itemIds.Count);
            var sb  = new System.Text.StringBuilder((end - i) * 8);
            for (var j = i; j < end; j++)
            {
                if (j > i) sb.Append(',');
                sb.Append(itemIds[j]);
            }

            var hqParam = hqFilter.HasValue ? $"&hq={(hqFilter.Value ? 1 : 0)}" : string.Empty;
            var url     = string.Concat(BaseUrl, "/", worldOrDc, "/", sb.ToString(), $"?listings={listingCount}&entries=0{hqParam}");
            var success = false;

            for (var attempt = 0; attempt <= MaxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    var delay = RetryDelayMs * attempt;
                    await Task.Delay(delay, ct);
                }

                var countBefore = results.Count;
                var (json, statusCode) = await FetchWithStatusAsync(url, ct);
                if (json != null)
                {
                    ParseMarketResponse(json, results);
                    success = true;
                    break;
                }

                GatherBuddy.Log.Warning(
                    $"[Marketboard] Batch {i / MaxItemsPerBatch} attempt {attempt}: HTTP {statusCode}. IDs: {sb}");

                if (statusCode == 404) break;
            }

            if (!success)
            {
                for (var j = i; j < end; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(InterBatchDelayMs, ct);

                    var singleUrl = string.Concat(BaseUrl, "/", worldOrDc, "/", itemIds[j].ToString(), $"?listings={listingCount}&entries=0{hqParam}");
                    var (singleJson, _) = await FetchWithStatusAsync(singleUrl, ct);
                    if (singleJson != null)
                    {
                        ParseMarketResponse(singleJson, results);
                    }
                }
            }
        }

        return results;
    }

    private async Task<(string? Json, int StatusCode)> FetchWithStatusAsync(string url, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            var response   = await _http.GetAsync(url, ct);
            var statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode) return (null, statusCode);

            if (response.Content.Headers.ContentLength > MaxResponseSizeBytes)
                return (null, statusCode);

            var json = await response.Content.ReadAsStringAsync(ct);
            return json.Length > MaxResponseSizeBytes ? (null, statusCode) : (json, statusCode);
        }
        catch (TaskCanceledException)
        {
            GatherBuddy.Log.Warning($"[Marketboard] Request timed out: {url}");
            return (null, 408);
        }
        catch (HttpRequestException ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] Request failed: {url} — {ex.Message}");
            return (null, 0);
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static void ParseMarketResponse(string json, List<MarketItemData> results)
    {
        try
        {
            using var doc  = JsonDocument.Parse(json);
            var       root = doc.RootElement;

            if (root.TryGetProperty("items", out var itemsObj) &&
                itemsObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in itemsObj.EnumerateObject())
                {
                    var data = ParseSingleItem(prop.Value);
                    if (data != null) results.Add(data);
                }
            }
            else
            {
                var data = ParseSingleItem(root);
                if (data != null) results.Add(data);
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[Marketboard] Failed to parse market response: {ex.Message}");
        }
    }

    private static MarketItemData? ParseSingleItem(JsonElement el)
    {
        if (!el.TryGetProperty("itemID", out var idEl)) return null;

        var listings = new List<MarketListing>();
        if (el.TryGetProperty("listings", out var listingsEl) &&
            listingsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in listingsEl.EnumerateArray())
            {
                listings.Add(new MarketListing
                {
                    PricePerUnit = GetInt(entry, "pricePerUnit"),
                    Quantity     = GetInt(entry, "quantity"),
                    IsHq         = entry.TryGetProperty("hq", out var hqEl) && hqEl.GetBoolean(),
                    WorldName    = GetString(entry, "worldName"),
                });
            }
        }

        return new MarketItemData
        {
            ItemId   = idEl.GetUInt32(),
            MinPrice = GetFloat(el, "minPrice"),
            Listings = listings,
        };
    }

    private static float GetFloat(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.TryGetSingle(out var f) ? f : 0f;

    private static int GetInt(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    public void Dispose()
    {
        _http.Dispose();
        _throttle.Dispose();
    }
}
