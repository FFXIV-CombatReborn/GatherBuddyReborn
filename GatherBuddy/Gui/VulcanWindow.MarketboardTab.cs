using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using GatherBuddy.Marketboard;
using GatherBuddy.RebornUI;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private uint _mbSelectedItemId = 0;
    private uint _mbDetailLastItemId = 0;
    private int _mbDetailScopeIndex = 0;
    private bool _mbRequestFocus = false;
    private List<uint> _mbHistorySnapshot = [];
    private List<uint> _mbFilteredSnapshot = [];
    private string _mbSearch = string.Empty;
    private bool _mbFilterDirty = true;
    private DateTime _mbHistoryRefresh = DateTime.MinValue;
    private List<string> _mbScopeOptions = [];
    private List<bool> _mbScopeIsDc = [];
    private DateTime _mbScopeRefresh = DateTime.MinValue;

    private void DrawMarketboardPage()
    {
        var svc = GatherBuddy.MarketboardService;
        if (svc == null)
        {
            M3Widgets.EmptyState("##mbUnavailable", "Marketboard unavailable", "The marketboard service could not start.", FontAwesomeIcon.ChartLine);
            return;
        }

        if ((DateTime.UtcNow - _mbHistoryRefresh).TotalSeconds > 0.5)
        {
            _mbHistorySnapshot = svc.GetHistorySnapshot();
            _mbHistoryRefresh = DateTime.UtcNow;
            _mbFilterDirty = true;
        }

        if ((DateTime.UtcNow - _mbScopeRefresh).TotalMinutes > 5 || _mbScopeOptions.Count == 0)
        {
            RefreshScopeOptions(svc);
        }

        if (M3Widgets.Button("##mbRefreshAll", "Refresh all", M3ButtonStyle.Tonal, FontAwesomeIcon.Sync, enabled: _mbHistorySnapshot.Count > 0,
                tooltip: "Look up the prices of every item in the history again."))
        {
            svc.RefreshAll();
        }

        ImGui.SameLine(0, M3.Space2);
        if (M3Widgets.Button("##mbClear", "Clear history", M3ButtonStyle.Text, FontAwesomeIcon.Trash, enabled: _mbHistorySnapshot.Count > 0))
        {
            svc.Clear();
            _mbSelectedItemId = 0;
            _mbDetailLastItemId = 0;
            _mbDetailScopeIndex = 0;
            _mbHistorySnapshot = [];
            _mbFilteredSnapshot = [];
        }

        ImGui.Dummy(new Vector2(0, M3.Space1));
        var avail = ImGui.GetContentRegionAvail();
        var leftWidth = VulcanUiScaling.Scaled(290f);
        using (var left = M3Panel.Begin("##mbLeftPanel", new Vector2(leftWidth, avail.Y), false, VulcanUiStyle.PanelBackground))
        {
            if (left)
            {
                DrawMarketboardHistoryList(svc);
            }
        }

        ImGui.SameLine(0, M3.Space2);
        using var right = M3Panel.Begin("##mbRightPanel", new Vector2(0, avail.Y));
        if (right)
        {
            DrawMarketboardDetail(svc);
        }
    }
    private void RefreshScopeOptions(MarketboardService svc)
    {
        var dc = MarketboardService.GetDataCenter();
        var worlds = MarketboardService.GetDcWorlds();
        var otherDcs = svc.GetOtherDcs();

        var opts = new List<string>(1 + worlds.Count + otherDcs.Count);
        var isDc = new List<bool>(opts.Capacity);

        if (!string.IsNullOrEmpty(dc))
        { opts.Add(dc); isDc.Add(true); }
        foreach (var w in worlds)
        { opts.Add(w); isDc.Add(false); }
        foreach (var d in otherDcs)
        { opts.Add(d); isDc.Add(true); }

        _mbScopeOptions = opts;
        _mbScopeIsDc = isDc;
        _mbScopeRefresh = DateTime.UtcNow;
    }

    private void UpdateFilteredHistory(MarketboardService svc)
    {
        _mbFilterDirty = false;
        if (string.IsNullOrWhiteSpace(_mbSearch))
        {
            _mbFilteredSnapshot = [with(_mbHistorySnapshot)];
            return;
        }
        var search = _mbSearch;
        _mbFilteredSnapshot = _mbHistorySnapshot.FindAll(id => svc.GetItemName(id).Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private void DrawMarketboardHistoryList(MarketboardService svc)
    {
        if (M3Widgets.SearchField("##mbsearch", "Search", ref _mbSearch, ImGui.GetContentRegionAvail().X))
        {
            _mbFilterDirty = true;
        }

        ImGui.Dummy(new Vector2(0, M3.Space1));
        if (_mbHistorySnapshot.Count == 0)
        {
            M3Widgets.EmptyState("##mbEmpty", "No lookups yet", "Right-click any item in the Materials window to look up its price.",
                FontAwesomeIcon.Search);
            return;
        }

        if (_mbFilterDirty)
        {
            UpdateFilteredHistory(svc);
        }

        if (_mbFilteredSnapshot.Count == 0)
        {
            M3Text.Draw("No items match your search.", M3TextStyle.Faint);
            return;
        }

        var iconSize = VulcanUiScaling.Scaled(28f);
        var dcScope = _mbScopeOptions.Count > 0 ? _mbScopeOptions[0] : MarketboardService.GetDataCenter();
        using var rows = ImRaii.Child("##mbRows", new Vector2(-1, -1));
        if (!rows)
        {
            return;
        }

        Ui.ClippedDraw(_mbFilteredSnapshot, itemId =>
        {
            var name = svc.GetItemName(itemId);
            var data = svc.GetCached(itemId, dcScope);
            var (status, statusColor) = svc.IsPending(itemId, dcScope)
                ? ("...", M3.Scheme.Warning)
                : svc.HasError(itemId, dcScope) || data == null
                    ? ("N/A", M3Text.ColorOf(M3TextStyle.Faint))
                    : ($"{data.MinPrice:N0}g", M3.Scheme.Success);

            if (Ui.IconListRow($"##mb_{itemId}", svc.GetItemIcon(itemId), name, _mbSelectedItemId == itemId, iconSize, status, statusColor))
            {
                _mbSelectedItemId = itemId;
            }

            using var menu = M3Menu.BeginContext($"##mbctx_{itemId}");
            if (menu.IsOpen && M3Menu.Item("##remove", "Remove from history", FontAwesomeIcon.Trash))
            {
                svc.RemoveFromHistory(itemId);
                if (_mbSelectedItemId == itemId)
                {
                    _mbSelectedItemId = 0;
                    _mbDetailLastItemId = 0;
                }

                _mbHistorySnapshot.Remove(itemId);
                _mbFilterDirty = true;
            }
        }, iconSize + M3.Space2 + ImGui.GetStyle().ItemSpacing.Y);
    }

    private void DrawMarketboardDetail(MarketboardService svc)
    {
        if (_mbSelectedItemId == 0)
        {
            M3Widgets.EmptyState("##mbNoItem", "No item selected", "Pick an item on the left to see its prices.", FontAwesomeIcon.ChartLine);
            return;
        }

        var itemId = _mbSelectedItemId;
        if (itemId != _mbDetailLastItemId)
        {
            _mbDetailLastItemId = itemId;
            _mbDetailScopeIndex = 0;
        }

        var scope = _mbScopeOptions.Count > _mbDetailScopeIndex ? _mbScopeOptions[_mbDetailScopeIndex] : MarketboardService.GetDataCenter();
        var isDcScope = _mbDetailScopeIndex < _mbScopeIsDc.Count && _mbScopeIsDc[_mbDetailScopeIndex];
        var name = svc.GetItemName(itemId);
        var iconId = svc.GetItemIcon(itemId);
        var data = svc.GetCached(itemId, scope);

        var largeIcon = VulcanUiScaling.Scaled(48f);
        var start = ImGui.GetCursorScreenPos();
        if (iconId > 0)
        {
            M3Widgets.GameIcon(iconId, largeIcon);
            ImGui.SameLine(0, M3.Space3);
        }

        using (ImRaii.Group())
        {
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, start.Y + (largeIcon - M3.FitText(19f, 0f)) * 0.5f));
            using (ImRaii.PushFont(M3.TitleMedium))
            {
                ImGui.TextUnformatted(name);
            }
        }

        ImGui.Dummy(new Vector2(0, M3.Space2));
        var scopeIndex = _mbDetailScopeIndex;
        if (M3Widgets.Combo("##mbdetailscope", ref scopeIndex, _mbScopeOptions.Count,
                i => i < _mbScopeIsDc.Count && _mbScopeIsDc[i] ? $"Data center: {_mbScopeOptions[i]}" : _mbScopeOptions[i], VulcanUiScaling.Scaled(220f),
                (isDcScope ? "Data center: " : string.Empty) + scope)
         && scopeIndex != _mbDetailScopeIndex)
        {
            _mbDetailScopeIndex = scopeIndex;
            svc.QueueLookup(itemId, name, iconId, _mbScopeOptions[scopeIndex]);
        }

        ImGui.SameLine(0, M3.Space2);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (M3Widgets.ComboHeight - M3Widgets.IconButtonSize) * 0.5f);
        if (M3Widgets.IconButton("##mbrefresh", FontAwesomeIcon.Sync, "Look the price up again"))
        {
            svc.ForceRefresh(itemId, scope);
        }

        ImGui.SameLine(0, M3.Space1);
        if (M3Widgets.IconButton("##mbweb", FontAwesomeIcon.ExternalLinkAlt, $"Open https://universalis.app/market/{itemId}"))
        {
            try
            {
                Process.Start(new ProcessStartInfo($"https://universalis.app/market/{itemId}") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                GatherBuddy.Log.Warning($"[Marketboard] Failed to open Universalis URL: {ex.Message}");
            }
        }

        ImGui.Dummy(new Vector2(0, M3.Space2));
        if (svc.IsPending(itemId, scope))
        {
            M3Widgets.LinearProgressIndeterminate(new Vector2(ImGui.GetContentRegionAvail().X, 4f * M3.Scale));
            M3Text.Draw("Fetching market data...", M3TextStyle.Muted);
            return;
        }

        if (svc.HasError(itemId, scope) || data == null)
        {
            M3Widgets.Banner("##mbNoData", "No market data. The item may not be tradeable, or the request failed.", M3Severity.Warning,
                FontAwesomeIcon.ExclamationTriangle);
            return;
        }

        DrawListingTable(data.Listings, isDcScope);
        var age = DateTime.UtcNow - svc.GetFetchTime(itemId, scope);
        var ageText = age.TotalSeconds < 60 ? "just now"
            : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} minutes ago"
            : $"{(int)age.TotalHours} hours ago";
        ImGui.Dummy(new Vector2(0, M3.Space1));
        M3Text.Draw($"Updated {ageText}", M3TextStyle.Faint);
    }

    private static void DrawListingTable(List<MarketListing> listings, bool showWorld)
    {
        var nq = new List<MarketListing>(listings.Count);
        var hq = new List<MarketListing>();
        foreach (var listing in listings)
        {
            (listing.IsHq ? hq : nq).Add(listing);
        }

        DrawListingSection("Normal quality", nq, hq.Count > 0 ? 10 : 20, showWorld, M3.Scheme.OnSurface);
        if (hq.Count > 0)
        {
            DrawListingSection("High quality", hq, 10, showWorld, M3.Scheme.Info);
        }
    }

    private static void DrawListingSection(string label, List<MarketListing> items, int maxCount, bool showWorld, Vector4 priceColor)
    {
        M3Widgets.SectionLabel(label);
        if (items.Count == 0)
        {
            M3Text.Draw("No listings.", M3TextStyle.Faint);
            return;
        }

        using var style = M3Table.PushStyle();
        using var table = ImRaii.Table($"##mb{label}tbl", showWorld ? 3 : 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX,
            new Vector2(-1, 0));
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, VulcanUiScaling.Scaled(130f));
        ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, VulcanUiScaling.Scaled(70f));
        if (showWorld)
        {
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 1f);
        }

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        M3Table.Header("Price", center: false);
        ImGui.TableNextColumn();
        M3Table.Header("Quantity", center: false);
        if (showWorld)
        {
            ImGui.TableNextColumn();
            M3Table.Header("World", center: false);
        }

        var count = Math.Min(items.Count, maxCount);
        for (var i = 0; i < count; i++)
        {
            var l = items[i];
            ImGui.TableNextColumn();
            Ui.Text($"{l.PricePerUnit:N0} gil", i == 0 ? M3.Scheme.Tertiary : priceColor);
            ImGui.TableNextColumn();
            Ui.Text($"×{l.Quantity}", M3.Scheme.OnSurfaceVariant);
            if (showWorld)
            {
                ImGui.TableNextColumn();
                Ui.Text(l.WorldName, M3.Scheme.OnSurfaceVariant);
            }
        }
    }
}