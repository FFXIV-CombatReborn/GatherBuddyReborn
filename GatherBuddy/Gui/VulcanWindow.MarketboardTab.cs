using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using ElliLib;
using ElliLib.Raii;
using GatherBuddy.Marketboard;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private uint         _mbSelectedItemId    = 0;
    private uint         _mbDetailLastItemId  = 0;
    private int          _mbDetailScopeIndex  = 0;
    private bool         _mbRequestFocus      = false;
    private List<uint>   _mbHistorySnapshot   = new();
    private List<uint>   _mbFilteredSnapshot  = new();
    private string       _mbSearch            = string.Empty;
    private bool         _mbFilterDirty       = true;
    private DateTime     _mbHistoryRefresh    = DateTime.MinValue;
    private List<string> _mbScopeOptions      = new();
    private List<bool>   _mbScopeIsDc         = new();
    private DateTime     _mbScopeRefresh      = DateTime.MinValue;

    private void DrawMarketboardTab()
    {
        IDisposable tabItem;
        bool        tabOpen;

        if (GatherBuddy.ControllerSupport != null && !_mbRequestFocus)
        {
            var handle = GatherBuddy.ControllerSupport.TabNavigation.TabItem("Marketboard##marketboardTab", 7, 8);
            tabItem = handle;
            tabOpen = handle;
        }
        else
        {
            ImRaii.IEndObject handle;
            if (_mbRequestFocus)
            {
                bool dummy = true;
                handle = ImRaii.TabItem("Marketboard##marketboardTab", ref dummy, ImGuiTabItemFlags.SetSelected);
            }
            else
            {
                handle = ImRaii.TabItem("Marketboard##marketboardTab");
            }
            tabItem = handle;
            tabOpen = handle.Success;
            if (tabOpen) _mbRequestFocus = false;
        }

        using (tabItem)
        {
            if (!tabOpen)
                return;

            var svc = GatherBuddy.MarketboardService;
            if (svc == null)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "Marketboard service unavailable.");
                return;
            }

            if ((DateTime.UtcNow - _mbHistoryRefresh).TotalSeconds > 0.5)
            {
                _mbHistorySnapshot = svc.GetHistorySnapshot();
                _mbHistoryRefresh  = DateTime.UtcNow;
                _mbFilterDirty     = true;
            }

            if ((DateTime.UtcNow - _mbScopeRefresh).TotalMinutes > 5 || _mbScopeOptions.Count == 0)
                RefreshScopeOptions(svc);

            if (ImGui.SmallButton("Clear All##mbclear"))
            {
                svc.Clear();
                _mbSelectedItemId   = 0;
                _mbDetailLastItemId = 0;
                _mbDetailScopeIndex = 0;
                _mbHistorySnapshot  = new();
                _mbFilteredSnapshot = new();
            }
            ImGui.SameLine(0, 6);
            if (ImGui.SmallButton("Refresh All##mbrefreshall"))
                svc.RefreshAll();

            ImGui.Separator();
            ImGui.Spacing();

            var avail     = ImGui.GetContentRegionAvail();
            const float leftWidth = 270f;

            using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
            {
                ImGui.BeginChild("##mbLeftPanel", new Vector2(leftWidth, avail.Y), true);
                DrawMarketboardHistoryList(svc);
                ImGui.EndChild();
            }

            ImGui.SameLine();

            using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
            {
                ImGui.BeginChild("##mbRightPanel", new Vector2(0, avail.Y), true);
                DrawMarketboardDetail(svc);
                ImGui.EndChild();
            }
        }
    }

    private void RefreshScopeOptions(MarketboardService svc)
    {
        var dc       = svc.GetDataCenter();
        var worlds   = svc.GetDcWorlds();
        var otherDcs = svc.GetOtherDcs();

        var opts = new List<string>(1 + worlds.Count + otherDcs.Count);
        var isDc = new List<bool>(opts.Capacity);

        if (!string.IsNullOrEmpty(dc)) { opts.Add(dc); isDc.Add(true); }
        foreach (var w in worlds)      { opts.Add(w);  isDc.Add(false); }
        foreach (var d in otherDcs)    { opts.Add(d);  isDc.Add(true); }

        _mbScopeOptions = opts;
        _mbScopeIsDc    = isDc;
        _mbScopeRefresh = DateTime.UtcNow;
    }

    private void UpdateFilteredHistory(MarketboardService svc)
    {
        _mbFilterDirty = false;
        if (string.IsNullOrWhiteSpace(_mbSearch))
        {
            _mbFilteredSnapshot = new List<uint>(_mbHistorySnapshot);
            return;
        }
        var search = _mbSearch;
        _mbFilteredSnapshot = _mbHistorySnapshot
            .Where(id => svc.GetItemName(id).Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void DrawMarketboardHistoryList(MarketboardService svc)
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##mbsearch", "Search...", ref _mbSearch, 128))
            _mbFilterDirty = true;

        ImGui.Spacing();

        if (_mbHistorySnapshot.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No lookups yet.");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Right-click any item in the");
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Materials window to search.");
            return;
        }

        if (_mbFilterDirty) UpdateFilteredHistory(svc);

        if (_mbFilteredSnapshot.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No results match your search.");
            return;
        }

        var iconSize   = new Vector2(28f, 28f);
        var itemHeight = iconSize.Y + ImGui.GetStyle().ItemSpacing.Y;
        var maxX       = ImGui.GetContentRegionMax().X;
        var dcScope    = _mbScopeOptions.Count > 0 ? _mbScopeOptions[0] : svc.GetDataCenter();
        var spacing    = ImGui.GetStyle().ItemSpacing.X;

        ImGuiClip.ClippedDraw(_mbFilteredSnapshot, itemId =>
        {
            var name      = svc.GetItemName(itemId);
            var iconId    = svc.GetItemIcon(itemId);
            var pending   = svc.IsPending(itemId, dcScope);
            var hasErr    = svc.HasError(itemId, dcScope);
            var data      = svc.GetCached(itemId, dcScope);
            var isSelected = _mbSelectedItemId == itemId;

            if (iconId > 0)
            {
                var wrap = Icons.DefaultStorage.TextureProvider
                    .GetFromGameIcon(new GameIconLookup(iconId))
                    .GetWrapOrDefault();
                if (wrap != null)
                    ImGui.Image(wrap.Handle, iconSize);
                else
                    ImGui.Dummy(iconSize);
            }
            else
            {
                ImGui.Dummy(iconSize);
            }

            ImGui.SameLine(0, 6);
            var textRowY = ImGui.GetCursorPosY() + (iconSize.Y - ImGui.GetTextLineHeight()) / 2f;
            ImGui.SetCursorPosY(textRowY);

            string statusLabel;
            Vector4 statusColor;
            if (pending)
            {
                statusLabel = " [...]";
                statusColor = ImGuiColors.DalamudOrange;
            }
            else if (hasErr || data == null)
            {
                statusLabel = " [N/A]";
                statusColor = ImGuiColors.DalamudGrey;
            }
            else
            {
                statusLabel = $"  {data.MinPrice:N0}g";
                statusColor = new Vector4(0.4f, 1f, 0.4f, 1f);
            }

            var statusW = ImGui.CalcTextSize(statusLabel).X + spacing;
            if (ImGui.Selectable($"{name}##mb_{itemId}", isSelected, ImGuiSelectableFlags.None,
                    new Vector2(maxX - ImGui.GetCursorPosX() - statusW, 0)))
                _mbSelectedItemId = itemId;

            var isCtxOpen = GatherBuddy.ControllerSupport != null
                ? GatherBuddy.ControllerSupport.ContextMenu.BeginPopupContextItemWithGamepad($"##mbctx_{itemId}", Dalamud.GamepadState)
                : ImGui.BeginPopupContextItem($"##mbctx_{itemId}");
            if (isCtxOpen)
            {
                if (ImGui.Selectable("Remove from history"))
                {
                    svc.RemoveFromHistory(itemId);
                    if (_mbSelectedItemId == itemId) { _mbSelectedItemId = 0; _mbDetailLastItemId = 0; }
                    _mbHistorySnapshot.Remove(itemId);
                    _mbFilterDirty = true;
                }
                ImGui.EndPopup();
            }

            ImGui.SameLine(0, spacing);
            ImGui.SetCursorPosY(textRowY);
            ImGui.TextColored(statusColor, statusLabel);
        }, itemHeight);
    }

    private void DrawMarketboardDetail(MarketboardService svc)
    {
        if (_mbSelectedItemId == 0)
        {
            var h = ImGui.GetContentRegionAvail().Y;
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + h / 2f - ImGui.GetTextLineHeight());
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 8);
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Select an item to view prices.");
            return;
        }

        var itemId = _mbSelectedItemId;

        if (itemId != _mbDetailLastItemId)
        {
            _mbDetailLastItemId = itemId;
            _mbDetailScopeIndex = 0;
        }

        var scope     = _mbScopeOptions.Count > _mbDetailScopeIndex ? _mbScopeOptions[_mbDetailScopeIndex] : svc.GetDataCenter();
        var isDcScope = _mbDetailScopeIndex < _mbScopeIsDc.Count && _mbScopeIsDc[_mbDetailScopeIndex];

        var name    = svc.GetItemName(itemId);
        var iconId  = svc.GetItemIcon(itemId);
        var pending = svc.IsPending(itemId, scope);
        var hasErr  = svc.HasError(itemId, scope);
        var data    = svc.GetCached(itemId, scope);

        var largeIcon = new Vector2(48f, 48f);

        if (iconId > 0)
        {
            var wrap = Icons.DefaultStorage.TextureProvider
                .GetFromGameIcon(new GameIconLookup(iconId))
                .GetWrapOrDefault();
            if (wrap != null)
            {
                ImGui.Image(wrap.Handle, largeIcon);
                ImGui.SameLine(0, 10);
            }
        }

        var lineH = ImGui.GetTextLineHeightWithSpacing();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (largeIcon.Y - lineH * 2f) / 2f);
        ImGui.TextColored(ImGuiColors.ParsedGold, name);

        var currentScopeLabel = (isDcScope ? $"DC: " : string.Empty) + scope;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.BeginCombo("##mbdetailscope", currentScopeLabel))
        {
            for (var i = 0; i < _mbScopeOptions.Count; i++)
            {
                var isdc = i < _mbScopeIsDc.Count && _mbScopeIsDc[i];
                var lbl  = isdc ? $"DC: {_mbScopeOptions[i]}" : _mbScopeOptions[i];
                if (ImGui.Selectable(lbl, _mbDetailScopeIndex == i) && _mbDetailScopeIndex != i)
                {
                    _mbDetailScopeIndex = i;
                    svc.QueueLookup(itemId, name, iconId, _mbScopeOptions[i]);
                }
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine(0, 8);
        if (ImGui.SmallButton("Refresh##mbrefresh"))
            svc.ForceRefresh(itemId, scope);
        ImGui.SameLine(0, 4);
        if (ImGui.SmallButton("Universalis##mbweb"))
        {
            try { Process.Start(new ProcessStartInfo($"https://universalis.app/market/{itemId}") { UseShellExecute = true }); }
            catch (Exception ex) { GatherBuddy.Log.Warning($"[Marketboard] Failed to open Universalis URL: {ex.Message}"); }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Open https://universalis.app/market/{itemId}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (pending)
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange, "Fetching market data...");
            return;
        }

        if (hasErr || data == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No market data available.");
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Item may not be tradeable,");
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "or the request failed.");
        }
        else
        {
            DrawListingTable(data.Listings, isDcScope);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            var fetchTime = svc.GetFetchTime(itemId, scope);
            var age       = DateTime.UtcNow - fetchTime;
            var ageText   = age.TotalSeconds < 60 ? "just now"
                : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m ago"
                : $"{(int)age.TotalHours}h ago";

            ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Updated: {ageText}");
        }
    }

    private static readonly Vector4 ColGold  = new(1.00f, 0.85f, 0.30f, 1f);
    private static readonly Vector4 ColWhite = new(1.00f, 1.00f, 1.00f, 1f);
    private static readonly Vector4 ColBlue  = new(0.50f, 0.85f, 1.00f, 1f);
    private static readonly Vector4 ColGrey  = ImGuiColors.DalamudGrey3;

    private static void DrawListingTable(List<MarketListing> listings, bool showWorld)
    {
        var nq = new List<MarketListing>();
        var hq = new List<MarketListing>();
        foreach (var l in listings)
        {
            if (l.IsHq) hq.Add(l);
            else        nq.Add(l);
        }

        var maxNq = hq.Count > 0 ? 10 : 20;
        DrawListingSection("NQ", nq, maxNq, showWorld, ColWhite);
        if (hq.Count > 0)
        {
            ImGui.Spacing();
            DrawListingSection("HQ", hq, 10, showWorld, ColBlue);
        }
    }

    private static void DrawListingSection(string label, List<MarketListing> items, int maxCount, bool showWorld, Vector4 priceColor)
    {
        ImGui.TextColored(ColGrey, label);
        ImGui.Separator();

        if (items.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "  No listings.");
            return;
        }

        var colCount   = showWorld ? 3 : 2;
        var tableFlags = ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg;
        if (!ImGui.BeginTable($"##mb{label}tbl", colCount, tableFlags, new Vector2(-1, 0)))
            return;

        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed,   120f);
        ImGui.TableSetupColumn("Qty",   ImGuiTableColumnFlags.WidthFixed,    55f);
        if (showWorld)
            ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        var count = Math.Min(items.Count, maxCount);
        for (var i = 0; i < count; i++)
        {
            var l = items[i];
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextColored(i == 0 ? ColGold : priceColor, $"{l.PricePerUnit:N0} gil");
            ImGui.TableSetColumnIndex(1);
            ImGui.TextColored(ColGrey, $"\u00d7{l.Quantity}");
            if (showWorld)
            {
                ImGui.TableSetColumnIndex(2);
                ImGui.TextColored(ColGrey, l.WorldName);
            }
        }

        ImGui.EndTable();
    }
}
