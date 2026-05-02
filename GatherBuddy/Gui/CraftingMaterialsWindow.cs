using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using ElliLib;
using ElliLib.Raii;
using ElliLib.Text;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Gui;

public class CraftingMaterialsWindow : Window
{
    private CraftingListEditor? _editor;
    private bool _matsOvercapPercent;
    private bool _matsShowPrecrafts;
    private bool _matsPreferVendors;
    private bool _matsKeepFulfilled;

    private static readonly Vector4 AccentGather = new(0.45f, 1.00f, 0.45f, 1f);
    private static readonly Vector4 AccentDrop   = new(1.00f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 AccentShop   = new(0.80f, 0.55f, 1.00f, 1f);
    private static readonly Vector4 AccentVendor = new(1.00f, 0.85f, 0.20f, 1f);
    private static readonly Vector4 AccentCraft  = new(0.35f, 0.90f, 0.90f, 1f);
    private enum RetainerColumnMode
    {
        None,
        Total,
        Split,
    }
    private readonly record struct MaterialEntry(
        uint ItemId,
        int Have,
        int RetNQ,
        int RetHQ,
        int Needed,
        int EffectiveAvailable,
        string Name,
        ushort IconId,
        bool IsPrecraft);
    private sealed record MaterialPanel(
        string Id,
        string Label,
        Vector4 Accent,
        List<MaterialEntry> Entries,
        RetainerColumnMode RetainerColumns,
        IReadOnlyList<VendorBuyListManager.VendorTargetRequest>? VendorTargets);
    private bool _cachedMaterialViewValid;
    private bool _cachedHasMaterials;
    private bool _cachedHasVisibleEntries;
    private int _cachedTotalMissing;
    private int _cachedTotalReady;
    private CraftingListEditor? _cachedMaterialViewEditor;
    private long _cachedMaterialViewVersion = -1;
    private bool _cachedMaterialViewShowPrecrafts;
    private bool _cachedMaterialViewPreferVendors;
    private bool _cachedMaterialViewKeepFulfilled;
    private bool _cachedMaterialViewShowRetainer;
    private List<MaterialPanel> _cachedPanels = [];

    public CraftingMaterialsWindow() : base("Materials###CraftingMaterials")
    {
        Size           = new Vector2(560, 520);
        SizeCondition  = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(1200, 1400),
        };
    }

    public void SetEditor(CraftingListEditor? editor)
    {
        if (!ReferenceEquals(_editor, editor))
            InvalidateMaterialView();
        _editor = editor;
    }

    public override void PreDraw()
    {
        if (_editor != null)
            WindowName = $"Materials \u2014 {_editor.ListName}###CraftingMaterials";
    }

    public override void Draw()
    {
        if (_editor == null)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No list open.");
            return;
        }

        using var theme = VulcanUiStyle.PushTheme();

        if (!_editor.HasCachedDisplayMaterials && !_editor.IsGeneratingMaterials)
            _editor.TriggerMaterialsRegeneration();

        if (_editor.IsGeneratingMaterials)
        {
            ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.9f, 1), "Calculating materials...");
            return;
        }
        var showRetainer = AllaganTools.Enabled;
        if (ShouldRebuildMaterialView(showRetainer))
            RebuildMaterialView(showRetainer);

        if (!_cachedHasMaterials)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), "No materials needed.");
            return;
        }

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), $"{_cachedTotalMissing} missing  ·  {_cachedTotalReady} ready");
        ImGui.SameLine();
        ImGui.Checkbox("150%##overcap", ref _matsOvercapPercent);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show % beyond 100 when you have more than needed");
        ImGui.SameLine();
        ImGui.Checkbox("Precrafts##precrafts", ref _matsShowPrecrafts);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Include intermediate craftable components and non-gear final craftables");
        ImGui.SameLine();
        ImGui.Checkbox("Prefer Vendors##preferVendors", ref _matsPreferVendors);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Default priority:\n" +
                "  Gather > Fish > Scrip > Drops > Craft > Vendor > Tomes > Other\n\n" +
                "When ON: Gil Vendor overrides Gather, Fish, Drops, and Craft\n" +
                "for items also sold at a Gil shop.");
        ImGui.SameLine();
        ImGui.Checkbox("Keep Fulfilled##keepFulfilled", ref _matsKeepFulfilled);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Keep fulfilled materials visible even when \"Skip if Already Have Enough\" is enabled on the list.");
        if (showRetainer)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Refresh Retainers"))
            {
                _editor.InvalidateRetainerSnapshot();
                InvalidateMaterialView();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Refresh retainer NQ/HQ counts. Automatic refresh is disabled here to avoid UI hitching.");
        }
        ImGui.Separator();
        if (!_cachedHasVisibleEntries)
        {
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1f), "All materials ready.");
            return;
        }

        var avail   = ImGui.GetContentRegionAvail();
        var spacing = ImGui.GetStyle().ItemSpacing;
        var panelW  = (avail.X - spacing.X) / 2f;

        var rows   = (_cachedPanels.Count + 1) / 2;
        var panelH = (avail.Y - spacing.Y * (rows - 1)) / rows;
        for (var i = 0; i < _cachedPanels.Count; i++)
        {
            var panel = _cachedPanels[i];
            var isLast   = i == _cachedPanels.Count - 1;
            var spanFull = isLast && _cachedPanels.Count % 2 == 1;
            DrawMaterialPanel(panel.Id, panel.Label, panel.Accent, panel.Entries, panel.RetainerColumns, spanFull ? avail.X : panelW, panelH, panel.VendorTargets);
            if (!spanFull && i % 2 == 0)
                ImGui.SameLine();
        }
    }

    private void InvalidateMaterialView()
    {
        _cachedMaterialViewValid = false;
        _cachedHasMaterials = false;
        _cachedHasVisibleEntries = false;
        _cachedTotalMissing = 0;
        _cachedTotalReady = 0;
        _cachedMaterialViewEditor = null;
        _cachedMaterialViewVersion = -1;
        _cachedPanels = [];
    }

    private bool ShouldRebuildMaterialView(bool showRetainer)
    {
        if (_editor == null)
            return false;

        if (!_cachedMaterialViewValid || !ReferenceEquals(_cachedMaterialViewEditor, _editor))
            return true;
        if (_cachedMaterialViewVersion != _editor.MaterialCacheVersion)
            return true;
        if (_cachedMaterialViewShowPrecrafts != _matsShowPrecrafts
         || _cachedMaterialViewPreferVendors != _matsPreferVendors
         || _cachedMaterialViewKeepFulfilled != _matsKeepFulfilled
         || _cachedMaterialViewShowRetainer != showRetainer)
            return true;

        return false;
    }

    private void RebuildMaterialView(bool showRetainer)
    {
        if (_editor == null)
            return;

        try
        {
            var materials = _editor.GetDisplayMaterials();
            _cachedHasMaterials = materials.Count > 0;
            _cachedPanels = [];
            _cachedTotalMissing = 0;
            _cachedTotalReady = 0;
            _cachedHasVisibleEntries = false;

            if (!_cachedHasMaterials)
            {
                UpdateMaterialViewCacheMetadata(showRetainer);
                return;
            }

            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            if (itemSheet == null)
            {
                UpdateMaterialViewCacheMetadata(showRetainer);
                return;
            }

            var countRetainersTowardNeed = showRetainer && _editor.RetainerRestockEnabled;
            var hideSatisfiedRows = _editor.SkipIfEnoughEnabled && !_matsKeepFulfilled;
            var craftMaterials = _matsShowPrecrafts
                ? _editor.GetDisplayPrecraftMaterials()
                : null;
            var snapshotItemIds = materials.Keys.Concat(craftMaterials != null ? craftMaterials.Keys : Enumerable.Empty<uint>());
            var retainerSnapshot = showRetainer
                ? _editor.GetRetainerSnapshot(snapshotItemIds)
                : RetainerItemSnapshot.Empty;

            var gatherList = new List<MaterialEntry>();
            var dropList = new List<MaterialEntry>();
            var shopList = new List<MaterialEntry>();
            var vendorList = new List<MaterialEntry>();
            List<MaterialEntry>? craftList = _matsShowPrecrafts ? [] : null;

            void AddEntry(MaterialEntry entry)
            {
                if (entry.EffectiveAvailable < entry.Needed)
                    _cachedTotalMissing++;
                else
                    _cachedTotalReady++;

                if (hideSatisfiedRows && entry.EffectiveAvailable >= entry.Needed)
                    return;

                _cachedHasVisibleEntries = true;
                switch (MaterialSourceClassifier.Classify(entry.ItemId, _matsPreferVendors))
                {
                    case MaterialSource.Gatherable:
                    case MaterialSource.Fish:
                        gatherList.Add(entry);
                        break;
                    case MaterialSource.Drop:
                        dropList.Add(entry);
                        break;
                    case MaterialSource.Scrip:
                    case MaterialSource.SpecialCurrency:
                        shopList.Add(entry);
                        break;
                    case MaterialSource.Craftable when craftList != null:
                        craftList.Add(entry);
                        break;
                    case MaterialSource.GilVendor:
                    case MaterialSource.Other:
                        vendorList.Add(entry);
                        break;
                }
            }

            foreach (var (itemId, needed) in materials)
            {
                if (TryCreateMaterialEntry(itemSheet, itemId, needed, false, retainerSnapshot, countRetainersTowardNeed, out var entry))
                    AddEntry(entry);
            }

            if (craftMaterials != null)
            {
                foreach (var (itemId, needed) in craftMaterials)
                {
                    if (TryCreateMaterialEntry(itemSheet, itemId, needed, true, retainerSnapshot, countRetainersTowardNeed, out var entry))
                        AddEntry(entry);
                }
            }

            SortEntries(gatherList);
            SortEntries(dropList);
            SortEntries(shopList);
            SortEntries(vendorList);
            if (craftList != null)
                SortEntries(craftList);

            var nonCraftRetainerMode = showRetainer ? RetainerColumnMode.Total : RetainerColumnMode.None;
            var craftRetainerMode = showRetainer ? RetainerColumnMode.Split : RetainerColumnMode.None;
            if (gatherList.Count > 0) _cachedPanels.Add(new MaterialPanel("##gather", "Gather", AccentGather, gatherList, nonCraftRetainerMode, null));
            if (dropList.Count > 0) _cachedPanels.Add(new MaterialPanel("##drop", "Drops / Bicolor", AccentDrop, dropList, nonCraftRetainerMode, null));
            if (shopList.Count > 0) _cachedPanels.Add(new MaterialPanel("##shop", "Special Currency", AccentShop, shopList, nonCraftRetainerMode, BuildVendorBuyListTargets(shopList)));
            if (vendorList.Count > 0) _cachedPanels.Add(new MaterialPanel("##vendor", "Vendor", AccentVendor, vendorList, nonCraftRetainerMode, BuildVendorBuyListTargets(vendorList)));
            if (craftList is { Count: > 0 }) _cachedPanels.Add(new MaterialPanel("##craft", "Craft", AccentCraft, craftList, craftRetainerMode, null));

            UpdateMaterialViewCacheMetadata(showRetainer);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingMaterialsWindow] Failed to rebuild materials view for list '{_editor.ListName}': {ex.Message}");
            InvalidateMaterialView();
        }
    }

    private void UpdateMaterialViewCacheMetadata(bool showRetainer)
    {
        _cachedMaterialViewValid = true;
        _cachedMaterialViewEditor = _editor;
        _cachedMaterialViewVersion = _editor?.MaterialCacheVersion ?? -1;
        _cachedMaterialViewShowPrecrafts = _matsShowPrecrafts;
        _cachedMaterialViewPreferVendors = _matsPreferVendors;
        _cachedMaterialViewKeepFulfilled = _matsKeepFulfilled;
        _cachedMaterialViewShowRetainer = showRetainer;
    }

    private bool TryCreateMaterialEntry(ExcelSheet<Item> itemSheet, uint itemId, int needed, bool isPrecraft,
        RetainerItemSnapshot retainerSnapshot, bool countRetainersTowardNeed, out MaterialEntry entry)
    {
        entry = default;
        if (!itemSheet.TryGetRow(itemId, out var item))
            return false;

        var have  = _editor?.GetInventoryCount(itemId) ?? 0;
        var retNQ = retainerSnapshot.GetCountNQ(itemId);
        var retHQ = retainerSnapshot.GetCountHQ(itemId);
        var effectiveAvailable = isPrecraft
            ? _editor?.GetDisplayCraftMaterialAvailableCount(itemId, retNQ, retHQ, countRetainersTowardNeed) ?? 0
            : _editor?.GetDisplayMaterialAvailableCount(itemId, retNQ, retHQ, countRetainersTowardNeed) ?? 0;
        entry = new MaterialEntry(itemId, have, retNQ, retHQ, needed, effectiveAvailable, item.Name.ExtractText(), item.Icon, isPrecraft);
        return true;
    }

    private static void SortEntries(List<MaterialEntry> entries)
    {
        entries.Sort((left, right) =>
        {
            var leftReady = left.EffectiveAvailable >= left.Needed;
            var rightReady = right.EffectiveAvailable >= right.Needed;
            var readyComparison = leftReady.CompareTo(rightReady);
            if (readyComparison != 0)
                return readyComparison;
            return string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });
    }

    private void DrawMaterialPanel(
        string id, string label, Vector4 accent,
        IReadOnlyList<MaterialEntry> entries,
        RetainerColumnMode retainerColumnMode, float width, float height, IReadOnlyList<VendorBuyListManager.VendorTargetRequest>? vendorTargets)
    {
        static void DrawCenteredHeader(string text, string? tooltip = null)
        {
            var textWidth = ImGui.CalcTextSize(text).X;
            var availableWidth = ImGui.GetContentRegionAvail().X;
            var offset = (availableWidth - textWidth) * 0.5f;
            if (offset > 0f)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);

            ImGui.TextUnformatted(text);
            if (tooltip != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
        }

        using (VulcanUiStyle.PushPanel())
        {
            ImGui.BeginChild(id, new Vector2(width, height), true);
            var colCount = retainerColumnMode switch
            {
                RetainerColumnMode.None  => 4,
                RetainerColumnMode.Total => 5,
                RetainerColumnMode.Split => 6,
                _                        => 4,
            };
            const float numW = 36f;
            const float barW = 46f;
            var tableFlags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg
                           | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit;

            if (ImGui.BeginTable($"{id}_tbl", colCount, tableFlags, new Vector2(0, ImGui.GetContentRegionAvail().Y)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("",     ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, numW);
                switch (retainerColumnMode)
                {
                    case RetainerColumnMode.Total:
                        ImGui.TableSetupColumn("Ret", ImGuiTableColumnFlags.WidthFixed, numW);
                        break;
                    case RetainerColumnMode.Split:
                        ImGui.TableSetupColumn("RNQ", ImGuiTableColumnFlags.WidthFixed, numW);
                        ImGui.TableSetupColumn("RHQ", ImGuiTableColumnFlags.WidthFixed, numW);
                        break;
                }
                ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, numW);
                ImGui.TableSetupColumn("%",    ImGuiTableColumnFlags.WidthFixed, barW);
                var needIdx = retainerColumnMode switch
                {
                    RetainerColumnMode.None  => 2,
                    RetainerColumnMode.Total => 3,
                    RetainerColumnMode.Split => 4,
                    _                        => 2,
                };

                ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                ImGui.TableSetColumnIndex(0);
                using (ImRaii.PushColor(ImGuiCol.Text, accent))
                    ImGui.TableHeader(label);
                ImGui.TableSetColumnIndex(1);
                DrawCenteredHeader("Have");
                switch (retainerColumnMode)
                {
                    case RetainerColumnMode.Total:
                        ImGui.TableSetColumnIndex(2);
                        DrawCenteredHeader("Ret", "Retainer total (via Allagan Tools)");
                        break;
                    case RetainerColumnMode.Split:
                        ImGui.TableSetColumnIndex(2);
                        DrawCenteredHeader("RNQ", "Retainer NQ (via Allagan Tools)");
                        ImGui.TableSetColumnIndex(3);
                        DrawCenteredHeader("RHQ", "Retainer HQ (via Allagan Tools)");
                        break;
                }
                ImGui.TableSetColumnIndex(needIdx);
                DrawCenteredHeader("Need");
                ImGui.TableSetColumnIndex(needIdx + 1);
                DrawCenteredHeader("%");

                if (entries.Count == 0)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1f), "  \u2014");
                }
                else
                {
                    var clipper = ImGui.ImGuiListClipper();
                    clipper.Begin(entries.Count);
                    while (clipper.Step())
                    {
                        for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                            DrawPanelRow(entries[i], retainerColumnMode, vendorTargets);
                    }
                    clipper.End();
                    clipper.Destroy();
                }

                ImGui.EndTable();
            }

            ImGui.EndChild();
        }
    }

    private void DrawPanelRow(MaterialEntry entry, RetainerColumnMode retainerColumnMode,
        IReadOnlyList<VendorBuyListManager.VendorTargetRequest>? vendorTargets)
    {
        var itemId            = entry.ItemId;
        var have              = entry.Have;
        var retNQ             = entry.RetNQ;
        var retHQ             = entry.RetHQ;
        var needed            = entry.Needed;
        var name              = entry.Name;
        var iconId            = entry.IconId;
        var effectiveAvailable = entry.EffectiveAvailable;
        var satisfied         = effectiveAvailable >= needed;
        var isPrecraft        = entry.IsPrecraft;
        ImGui.TableNextRow();
        Vector4 rowColor = (satisfied, isPrecraft) switch
        {
            (true,  false) => new Vector4(0.15f, 0.50f, 0.15f, 0.25f),
            (true,  true)  => new Vector4(0.05f, 0.40f, 0.45f, 0.25f),
            (false, false) => new Vector4(0.50f, 0.15f, 0.15f, 0.25f),
            (false, true)  => new Vector4(0.55f, 0.35f, 0.05f, 0.25f),
        };
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(rowColor));

        var lineH    = ImGui.GetTextLineHeight();
        var iconSize = new Vector2(lineH, lineH);

        static string Trunc(int v) => v > 9999 ? "9999" : v.ToString();
        void CenterNum(int raw, Vector4 color)
        {
            var s   = Trunc(raw);
            var off = (ImGui.GetColumnWidth() - ImGui.CalcTextSize(s).X) * 0.5f;
            if (off > 0f) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + off);
            ImGui.TextColored(color, s);
            if (raw > 9999 && ImGui.IsItemHovered())
                ImGui.SetTooltip(raw.ToString());
        }

        ImGui.TableNextColumn();
        var icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId));
        if (icon.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, iconSize);
        else
            ImGui.Dummy(iconSize);
        ImGui.SameLine(0, 4);
        ImUtf8.CopyOnClickSelectable(name.AsSpan());
        if (ImGui.BeginPopupContextItem($"##mbctx_{itemId}_{(isPrecraft ? 1 : 0)}_{(int)retainerColumnMode}"))
        {
            if (ImGui.Selectable("Create Link"))
                Communicator.Print(SeString.CreateItemLink(itemId));
            if (ImGui.Selectable("Search Marketboard"))
            {
                GatherBuddy.MarketboardService?.QueueLookup(itemId, name, iconId);
                GatherBuddy.VulcanWindow?.OpenToMarketboardItem(itemId);
            }

            DrawVendorBuyListPopup(entry, vendorTargets);
            ImGui.EndPopup();
        }

        var haveColor = satisfied ? new Vector4(0.4f, 1f, 0.4f, 1f) : new Vector4(1f, 0.45f, 0.45f, 1f);
        ImGui.TableNextColumn();
        CenterNum(have, haveColor);
        switch (retainerColumnMode)
        {
            case RetainerColumnMode.Total:
            {
                ImGui.TableNextColumn();
                var totalRetainer = retNQ + retHQ;
                var retainerColor = totalRetainer > 0 ? new Vector4(0.9f, 0.85f, 0.3f, 1f) : new Vector4(0.4f, 0.4f, 0.4f, 1f);
                if (totalRetainer > 0)
                    CenterNum(totalRetainer, retainerColor);
                else
                {
                    var off = (ImGui.GetColumnWidth() - ImGui.CalcTextSize("-").X) * 0.5f;
                    if (off > 0f) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + off);
                    ImGui.TextColored(retainerColor, "-");
                }
                break;
            }
            case RetainerColumnMode.Split:
            {
                ImGui.TableNextColumn();
                var nqColor = retNQ > 0 ? new Vector4(0.9f, 0.85f, 0.3f, 1f) : new Vector4(0.4f, 0.4f, 0.4f, 1f);
                if (retNQ > 0) CenterNum(retNQ, nqColor);
                else { var off = (ImGui.GetColumnWidth() - ImGui.CalcTextSize("-").X) * 0.5f; if (off > 0f) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + off); ImGui.TextColored(nqColor, "-"); }

                ImGui.TableNextColumn();
                var hqColor = retHQ > 0 ? new Vector4(0.5f, 0.85f, 1.0f, 1f) : new Vector4(0.4f, 0.4f, 0.4f, 1f);
                if (retHQ > 0) CenterNum(retHQ, hqColor);
                else { var off = (ImGui.GetColumnWidth() - ImGui.CalcTextSize("-").X) * 0.5f; if (off > 0f) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + off); ImGui.TextColored(hqColor, "-"); }
                break;
            }
            case RetainerColumnMode.None:
            default:
                break;
        }

        ImGui.TableNextColumn();
        CenterNum(needed, new Vector4(1f, 1f, 1f, 1f));

        ImGui.TableNextColumn();
        var ratio    = needed > 0 ? (float)effectiveAvailable / needed : 1f;
        var progress = Math.Clamp(ratio, 0f, 1f);
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram,
            satisfied ? new Vector4(0.2f, 0.65f, 0.2f, 0.9f) : new Vector4(0.65f, 0.2f, 0.2f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.12f, 0.12f, 0.12f, 0.9f));
        ImGui.ProgressBar(progress, new Vector2(ImGui.GetContentRegionAvail().X, lineH), "");
        ImGui.PopStyleColor(2);
        var pctText = _matsOvercapPercent ? $"{ratio * 100f:F0}%" : $"{progress * 100f:F0}%";
        var pctSize = ImGui.CalcTextSize(pctText);
        var barMin  = ImGui.GetItemRectMin();
        var barMax  = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddText(
            new Vector2(
                barMin.X + (barMax.X - barMin.X - pctSize.X) * 0.5f,
                barMin.Y + (barMax.Y - barMin.Y - pctSize.Y) * 0.5f),
            ImGui.GetColorU32(ImGuiCol.Text),
            pctText);
    }

    private void DrawVendorBuyListPopup(MaterialEntry entry, IReadOnlyList<VendorBuyListManager.VendorTargetRequest>? vendorTargets)
    {
        var buyListManager = GatherBuddy.VendorBuyListManager;
        if (buyListManager == null)
            return;

        var hasSingleTarget = TryCreateVendorBuyListTarget(entry, out var singleTarget);
        var hasBatchTargets = vendorTargets is { Count: > 0 };
        if (!hasSingleTarget && !hasBatchTargets)
            return;

        ImGui.Separator();

        if (hasSingleTarget)
        {
            DrawVendorBuyListExistingListMenu("Add to Existing List", new[] { singleTarget });
            if (ImGui.Selectable("Create New List"))
                OpenCreateVendorBuyListPopup(new[] { singleTarget });
        }

        if (hasBatchTargets && vendorTargets!.Count > 1)
        {
            DrawVendorBuyListExistingListMenu("Add Current Vendor View to Existing List", vendorTargets);
            if (ImGui.Selectable("Create New List from Current Vendor View"))
                OpenCreateVendorBuyListPopup(vendorTargets);
        }
    }

    private void DrawVendorBuyListExistingListMenu(string label, IReadOnlyList<VendorBuyListManager.VendorTargetRequest> targets)
    {
        var buyListManager = GatherBuddy.VendorBuyListManager;
        if (buyListManager == null)
            return;

        if (!ImGui.BeginMenu(label, buyListManager.Lists.Count > 0))
            return;

        foreach (var list in buyListManager.Lists.OrderByDescending(list => list.CreatedAt))
        {
            if (ImGui.Selectable(list.Name))
                AddTargetsToVendorBuyList(list.Id, list.Name, targets);
        }

        ImGui.EndMenu();
    }

    private void AddTargetsToVendorBuyList(Guid listId, string listName, IReadOnlyList<VendorBuyListManager.VendorTargetRequest> targets)
    {
        var buyListManager = GatherBuddy.VendorBuyListManager;
        if (buyListManager == null)
            return;

        var addedCount = buyListManager.TrySetTargets(listId, targets, selectList: true, openWindow: true, announce: true);
        if (addedCount == 0)
            GatherBuddy.Log.Debug($"[CraftingMaterialsWindow] Unable to add {targets.Count:N0} vendor target(s) to vendor buy list '{listName}'.");
    }

    private void OpenCreateVendorBuyListPopup(IReadOnlyList<VendorBuyListManager.VendorTargetRequest> targets)
    {
        var vendorBuyListWindow = GatherBuddy.VendorBuyListWindow;
        if (vendorBuyListWindow == null)
        {
            GatherBuddy.Log.Warning("[CraftingMaterialsWindow] Unable to open Create Vendor List popup: vendor buy list window unavailable.");
            return;
        }

        if (!vendorBuyListWindow.OpenCreateListPopup(targets))
            GatherBuddy.Log.Debug($"[CraftingMaterialsWindow] Unable to create a new vendor buy list for {targets.Count:N0} vendor target(s).");
    }

    private static List<VendorBuyListManager.VendorTargetRequest> BuildVendorBuyListTargets(IEnumerable<MaterialEntry> entries)
        => entries
            .Select(entry => TryCreateVendorBuyListTarget(entry, out var target)
                ? target
                : default)
            .Where(target => target.ItemId != 0 && target.TargetQuantity > 0)
            .GroupBy(target => target.ItemId)
            .Select(group => new VendorBuyListManager.VendorTargetRequest(group.Key, group.Max(target => target.TargetQuantity)))
            .ToList();

    private static bool TryCreateVendorBuyListTarget(MaterialEntry entry, out VendorBuyListManager.VendorTargetRequest target)
    {
        target = default;
        var buyListManager = GatherBuddy.VendorBuyListManager;
        if (buyListManager == null || !buyListManager.CanAddSupportedItem(entry.ItemId))
            return false;

        var missingQuantity = entry.Needed - entry.EffectiveAvailable;
        if (missingQuantity <= 0)
            return false;

        var currentCount = (uint)Math.Max(0, VendorBuyListManager.GetCurrentInventoryAndArmoryCount(entry.ItemId));
        target = new VendorBuyListManager.VendorTargetRequest(entry.ItemId, currentCount + (uint)missingQuantity);
        return true;
    }

}
