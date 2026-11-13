using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public class CraftingMaterialsWindow : M3PanelWindow
{
    public const string WindowId = "Materials###CraftingMaterials";
    private const uint MobDropIconId = 60041u;

    private const string PreferVendorsTooltip =
        "Default priority:\n"
      + "  Gather > Fish > Scrip > Drops > Craft > Vendor > Tomes > Other\n\n"
      + "When on, a gil vendor wins over Gather, Fish, Drops and Craft\n"
      + "for items also sold at a gil shop.";

    private static readonly Vector2 DefaultSize = new(640f, 560f);

    private static readonly WindowSizeConstraints DefaultConstraints = new()
    {
        MinimumSize = new Vector2(420f, 320f),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };

    private static readonly M3WindowAction[] MaterialsWindowActions =
    [
        new("##materials_tree", FontAwesomeIcon.Sitemap, "Show the crafting tree for this list"),
        new("##materials_vulcan", FontAwesomeIcon.Hammer, "Open Vulcan's crafting lists"),
    ];

    private static readonly Vector4 HueGather = new(0.45f, 1.00f, 0.45f, 1f);
    private static readonly Vector4 HueDrop = new(1.00f, 0.45f, 0.45f, 1f);
    private static readonly Vector4 HueShop = new(0.80f, 0.55f, 1.00f, 1f);
    private static readonly Vector4 HueVendor = new(1.00f, 0.85f, 0.20f, 1f);
    private static readonly Vector4 HueCraft = new(0.35f, 0.90f, 0.90f, 1f);
    private static readonly Vector4 HueMapMarker = new(0.45f, 0.80f, 1.00f, 1f);
    private static readonly Vector4 HueTeleport = new(0.60f, 0.95f, 0.60f, 1f);

    private static float MaterialRowIconSpacing
        => VulcanUiScaling.Scaled(4f);

    private static float MaterialRowIconGutter
        => VulcanUiScaling.Scaled(8f);

    private static float MinPanelWidth
        => VulcanUiScaling.Scaled(300f);

    private static float MinPanelHeight
        => VulcanUiScaling.Scaled(150f);

    private static readonly Dictionary<string, bool> MobDropZoneOpenStates = [with(StringComparer.Ordinal)];

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
        bool IsPrecraft,
        IReadOnlyList<CurrencyOption> CurrencyOptions,
        MobDropItemInfo DropInfo);

    private sealed record MaterialPanel(
        string Id,
        string Label,
        FontAwesomeIcon Icon,
        Vector4 Hue,
        List<MaterialEntry> Entries,
        int Missing,
        RetainerColumnMode RetainerColumns,
        IReadOnlyList<VendorBuyListManager.VendorTargetRequest>? VendorTargets,
        bool ShowCurrencyTotals = false,
        bool PreferBicolorDefault = false);

    private readonly record struct CurrencyOption(uint CurrencyItemId, uint CostPerItem, string Name, ushort IconId, VendorCurrencyGroup Group);
    private readonly record struct CurrencyTotal(uint CurrencyItemId, long Amount, string Name, ushort IconId);

    private CraftingListEditor? _editor;
    private bool _matsOvercapPercent;
    private bool _matsShowPrecrafts;
    private bool _matsPreferVendors;
    private bool _matsKeepFulfilled;
    private string? _windowNameListName;

    private readonly Dictionary<uint, uint> _userSelectedCurrencyByItem = [];
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
    private bool _cachedMobDropInfoInitialized;
    private List<MaterialPanel> _cachedPanels = [];

    public CraftingMaterialsWindow()
        : base(WindowId, "Materials", DefaultSize, DefaultConstraints)
    {
        BlocksGameInput = false;
    }

    public void SetEditor(CraftingListEditor? editor)
    {
        if (!ReferenceEquals(_editor, editor))
        {
            InvalidateMaterialView();
        }

        _editor = editor;
    }

    protected override M3WindowAction[] WindowActions
        => MaterialsWindowActions;

    protected override void OnWindowAction(int index)
    {
        switch (index)
        {
            case 0:
                {
                    var tree = GatherBuddy.CraftingTreeWindow;
                    if (tree != null)
                    {
                        tree.SetEditor(_editor);
                        tree.OpenAndRestore();
                    }

                    break;
                }
            case 1:
                GatherBuddy.VulcanWindow?.OpenToCraftingLists();
                break;
        }
    }

    protected override (string Text, Vector4? Color) Status
    {
        get
        {
            if (_editor == null)
            {
                return ("No list open", null);
            }

            if (_editor.IsGeneratingMaterials)
            {
                return ($"{_editor.ListName} · calculating materials...", M3.Scheme.Primary);
            }

            if (!_cachedMaterialViewValid || !_cachedHasMaterials)
            {
                return (_editor.ListName, null);
            }

            return _cachedTotalMissing == 0
                ? ($"{_editor.ListName} · all {_cachedTotalReady} ready", M3.Scheme.Success)
                : ($"{_editor.ListName} · {_cachedTotalMissing} missing · {_cachedTotalReady} ready", null);
        }
    }

    protected override void PreDrawFold()
    {
        var listName = _editor?.ListName;
        if (listName == _windowNameListName)
        {
            return;
        }

        _windowNameListName = listName;
        WindowName = listName == null ? WindowId : $"Materials — {listName}###CraftingMaterials";
    }

    protected override void DrawContents()
    {
        if (_editor == null)
        {
            if (M3Widgets.EmptyState("##materials_no_list", "No list open", "Open a crafting list in Vulcan to see the materials it needs.",
                    FontAwesomeIcon.ClipboardList, "Open Vulcan"))
            {
                GatherBuddy.VulcanWindow?.OpenToCraftingLists();
            }

            return;
        }

        if (!_editor.HasCachedDisplayMaterials && !_editor.IsGeneratingMaterials)
        {
            _editor.TriggerMaterialsRegeneration();
        }

        MobDropInfoCache.EnsureInitializeStarted();

        if (_editor.IsGeneratingMaterials)
        {
            DrawCalculating();
            return;
        }

        var showRetainer = AllaganTools.Enabled;
        if (ShouldRebuildMaterialView(showRetainer))
        {
            RebuildMaterialView(showRetainer);
        }

        if (!_cachedHasMaterials)
        {
            M3Widgets.EmptyState("##materials_none", "No materials needed",
                "Nothing on this list needs gathering, buying or crafting first.", FontAwesomeIcon.CheckCircle);
            return;
        }

        DrawToolbar(showRetainer);
        if (!_cachedHasVisibleEntries)
        {
            M3Widgets.EmptyState("##materials_all_ready", "All materials ready",
                "You have everything this list needs. Turn on Keep fulfilled to list them anyway.", FontAwesomeIcon.CheckDouble);
            return;
        }

        DrawPanels();
    }

    private static void DrawCalculating()
    {
        const string text = "Calculating materials...";
        var scale = M3.Scale;
        var width = ImGui.GetContentRegionAvail().X;
        var diameter = 32f * scale;

        ImGui.Dummy(new Vector2(width, 24f * scale));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (width - diameter) * 0.5f));
        M3Widgets.CircularProgress(diameter);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (width - ImGui.CalcTextSize(text).X) * 0.5f));
        M3Text.Draw(text, M3TextStyle.Muted);
    }

    private void DrawToolbar(bool showRetainer)
    {
        var flow = new M3Flow();
        FilterChip(ref flow, "##materials_overcap", "Over 100%", FontAwesomeIcon.Percent, ref _matsOvercapPercent,
            "Show percentages above 100 when you have more than you need.");
        FilterChip(ref flow, "##materials_precrafts", "Precrafts", FontAwesomeIcon.LayerGroup, ref _matsShowPrecrafts,
            "Include intermediate craftable components and non-gear final craftables.");
        FilterChip(ref flow, "##materials_prefer_vendors", "Prefer vendors", FontAwesomeIcon.Store, ref _matsPreferVendors,
            PreferVendorsTooltip);
        FilterChip(ref flow, "##materials_keep_fulfilled", "Keep fulfilled", FontAwesomeIcon.CheckDouble, ref _matsKeepFulfilled,
            "Keep fulfilled materials visible even when \"Skip if Already Have Enough\" is enabled on the list.");

        if (showRetainer && _editor != null)
        {
            const string refreshLabel = "Refresh retainers";
            flow.Next(M3Widgets.ChipWidth(refreshLabel, FontAwesomeIcon.Sync));
            if (M3Widgets.Chip("##materials_refresh_retainers", refreshLabel, false, FontAwesomeIcon.Sync,
                    "Refresh retainer NQ/HQ counts. They don't refresh on their own here, to avoid hitches."))
            {
                _editor.InvalidateRetainerSnapshot();
                InvalidateMaterialView();
                M3Snackbar.Show("Refreshed retainer counts.");
            }
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
    }

    private static void FilterChip(ref M3Flow flow, string id, string label, FontAwesomeIcon icon, ref bool value, string tooltip)
    {
        flow.Next(M3Widgets.ChipWidth(label, icon));
        if (M3Widgets.Chip(id, label, value, icon, tooltip))
        {
            value = !value;
        }
    }

    private void DrawPanels()
    {
        if (_cachedPanels.Count == 0)
        {
            return;
        }

        var avail = ImGui.GetContentRegionAvail();
        var spacing = ImGui.GetStyle().ItemSpacing;
        var columns = avail.X >= MinPanelWidth * 2f + spacing.X ? 2 : 1;
        var panelWidth = columns == 1 ? avail.X : MathF.Floor((avail.X - spacing.X) / 2f);
        var rows = (_cachedPanels.Count + columns - 1) / columns;
        var panelHeight = MathF.Max(MinPanelHeight, MathF.Floor((avail.Y - spacing.Y * (rows - 1)) / rows));

        for (var i = 0; i < _cachedPanels.Count; i++)
        {
            var isLast = i == _cachedPanels.Count - 1;
            var spanFull = columns == 1 || isLast && _cachedPanels.Count % 2 == 1;
            DrawMaterialPanel(_cachedPanels[i], spanFull ? avail.X : panelWidth, panelHeight);
            if (columns == 2 && !spanFull && i % 2 == 0)
            {
                ImGui.SameLine();
            }
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
        {
            return false;
        }

        if (!_cachedMaterialViewValid || !ReferenceEquals(_cachedMaterialViewEditor, _editor))
        {
            return true;
        }

        if (_cachedMaterialViewVersion != _editor.MaterialCacheVersion)
        {
            return true;
        }

        if (_cachedMaterialViewShowPrecrafts != _matsShowPrecrafts
         || _cachedMaterialViewPreferVendors != _matsPreferVendors
         || _cachedMaterialViewKeepFulfilled != _matsKeepFulfilled
         || _cachedMaterialViewShowRetainer != showRetainer)
        {
            return true;
        }

        if (_cachedMobDropInfoInitialized != MobDropInfoCache.IsInitialized)
        {
            return true;
        }

        return false;
    }

    private void RebuildMaterialView(bool showRetainer)
    {
        if (_editor == null)
        {
            return;
        }

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
            var retainerSnapshot = RetainerItemSnapshot.Empty;
            if (showRetainer)
            {
                var snapshotItemIds = new List<uint>(materials.Keys);
                if (craftMaterials != null)
                {
                    snapshotItemIds.AddRange(craftMaterials.Keys);
                }

                retainerSnapshot = _editor.GetRetainerSnapshot(snapshotItemIds);
            }

            var gatherList = new List<MaterialEntry>();
            var dropList = new List<MaterialEntry>();
            var shopList = new List<MaterialEntry>();
            var vendorList = new List<MaterialEntry>();
            List<MaterialEntry>? craftList = _matsShowPrecrafts ? [] : null;

            void AddEntry(MaterialEntry entry)
            {
                if (entry.EffectiveAvailable < entry.Needed)
                {
                    _cachedTotalMissing++;
                }
                else
                {
                    _cachedTotalReady++;
                }

                if (hideSatisfiedRows && entry.EffectiveAvailable >= entry.Needed)
                {
                    return;
                }

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
                {
                    AddEntry(entry);
                }
            }

            if (craftMaterials != null)
            {
                foreach (var (itemId, needed) in craftMaterials)
                {
                    if (TryCreateMaterialEntry(itemSheet, itemId, needed, true, retainerSnapshot, countRetainersTowardNeed, out var entry))
                    {
                        AddEntry(entry);
                    }
                }
            }

            SortEntries(gatherList);
            SortEntries(dropList);
            SortEntries(shopList);
            SortEntries(vendorList);
            if (craftList != null)
            {
                SortEntries(craftList);
            }

            var nonCraftRetainerMode = showRetainer ? RetainerColumnMode.Total : RetainerColumnMode.None;
            var craftRetainerMode = showRetainer ? RetainerColumnMode.Split : RetainerColumnMode.None;
            if (gatherList.Count > 0)
            {
                _cachedPanels.Add(new MaterialPanel("##gather", "Gather", FontAwesomeIcon.Leaf, HueGather, gatherList, CountMissing(gatherList),
                    nonCraftRetainerMode, null));
            }

            if (dropList.Count > 0)
            {
                _cachedPanels.Add(new MaterialPanel("##drop", "Drops / Bicolor", FontAwesomeIcon.Skull, HueDrop, dropList, CountMissing(dropList),
                    nonCraftRetainerMode, null, ShowCurrencyTotals: true, PreferBicolorDefault: true));
            }

            if (shopList.Count > 0)
            {
                _cachedPanels.Add(new MaterialPanel("##shop", "Special Currency", FontAwesomeIcon.Coins, HueShop, shopList, CountMissing(shopList),
                    nonCraftRetainerMode, BuildVendorBuyListTargets(shopList), ShowCurrencyTotals: true));
            }

            if (vendorList.Count > 0)
            {
                _cachedPanels.Add(new MaterialPanel("##vendor", "Vendor", FontAwesomeIcon.Store, HueVendor, vendorList, CountMissing(vendorList),
                    nonCraftRetainerMode, BuildVendorBuyListTargets(vendorList)));
            }

            if (craftList is { Count: > 0 })
            {
                _cachedPanels.Add(new MaterialPanel("##craft", "Craft", FontAwesomeIcon.Hammer, HueCraft, craftList, CountMissing(craftList),
                    craftRetainerMode, null));
            }

            UpdateMaterialViewCacheMetadata(showRetainer);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingMaterialsWindow] Failed to rebuild materials view for list '{_editor.ListName}': {ex.Message}");
            InvalidateMaterialView();
        }
    }

    private static int CountMissing(List<MaterialEntry> entries)
    {
        var missing = 0;
        foreach (var entry in entries)
        {
            if (entry.EffectiveAvailable < entry.Needed)
            {
                missing++;
            }
        }

        return missing;
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
        _cachedMobDropInfoInitialized = MobDropInfoCache.IsInitialized;
    }

    private bool TryCreateMaterialEntry(ExcelSheet<Item> itemSheet, uint itemId, int needed, bool isPrecraft,
        RetainerItemSnapshot retainerSnapshot, bool countRetainersTowardNeed, out MaterialEntry entry)
    {
        entry = default;
        if (!itemSheet.TryGetRow(itemId, out var item))
        {
            return false;
        }

        var have = _editor?.GetInventoryCount(itemId) ?? 0;
        var retNQ = retainerSnapshot.GetCountNQ(itemId);
        var retHQ = retainerSnapshot.GetCountHQ(itemId);
        var effectiveAvailable = isPrecraft
            ? _editor?.GetDisplayCraftMaterialAvailableCount(itemId, retNQ, retHQ, countRetainersTowardNeed) ?? 0
            : _editor?.GetDisplayMaterialAvailableCount(itemId, retNQ, retHQ, countRetainersTowardNeed) ?? 0;
        var skipExtras = CraftingRowIcons.IsElementalCrystal(itemId);
        var currencyOptions = skipExtras ? [] : ResolveCurrencyOptions(itemId);
        var dropInfo = skipExtras ? MobDropItemInfo.Empty : MobDropInfoCache.GetDropInfoForItem(itemId);
        entry = new MaterialEntry(itemId, have, retNQ, retHQ, needed, effectiveAvailable, item.Name.ExtractText(), item.Icon, isPrecraft, currencyOptions, dropInfo);
        return true;
    }

    private static List<CurrencyOption> ResolveCurrencyOptions(uint itemId)
    {
        Dictionary<uint, CurrencyOption>? options = null;
        AddCurrencyOptions(ref options, VendorShopResolver.GilShopEntries, itemId, false);
        AddCurrencyOptions(ref options, VendorShopResolver.SpecialShopEntries, itemId, false);
        AddCurrencyOptions(ref options, VendorShopResolver.GcShopEntries, itemId, true);
        if (options == null)
        {
            return [];
        }

        var sorted = new List<CurrencyOption>(options.Values);
        sorted.Sort(static (a, b) =>
        {
            var order = a.CostPerItem.CompareTo(b.CostPerItem);
            if (order != 0)
            {
                return order;
            }

            order = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return order != 0 ? order : a.CurrencyItemId.CompareTo(b.CurrencyItemId);
        });
        return sorted;
    }

    private static void AddCurrencyOptions(ref Dictionary<uint, CurrencyOption>? options, IReadOnlyList<VendorShopEntry> entries, uint itemId,
        bool currentGrandCompanyOnly)
    {
        foreach (var entry in entries)
        {
            if (entry.ItemId != itemId
             || entry.Cost == 0
             || entry.CurrencyItemId == 0
             || currentGrandCompanyOnly && !VendorShopResolver.MatchesCurrentGrandCompany(entry))
            {
                continue;
            }

            options ??= [];
            if (!options.TryGetValue(entry.CurrencyItemId, out var existing) || entry.Cost < existing.CostPerItem)
            {
                options[entry.CurrencyItemId] = new CurrencyOption(
                    entry.CurrencyItemId,
                    entry.Cost,
                    entry.CurrencyName,
                    ResolveCurrencyIconId(entry.CurrencyItemId),
                    entry.Group);
            }
        }
    }

    private static int IndexOfCurrencyOption(IReadOnlyList<CurrencyOption> options, uint currencyItemId)
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].CurrencyItemId == currencyItemId)
            {
                return i;
            }
        }

        return -1;
    }

    private uint GetSelectedCurrencyItemId(MaterialEntry entry, bool preferBicolor)
    {
        if (entry.CurrencyOptions.Count == 0)
        {
            return 0;
        }

        if (_userSelectedCurrencyByItem.TryGetValue(entry.ItemId, out var selected)
         && IndexOfCurrencyOption(entry.CurrencyOptions, selected) >= 0)
        {
            return selected;
        }

        if (preferBicolor)
        {
            for (var i = 0; i < entry.CurrencyOptions.Count; i++)
            {
                var option = entry.CurrencyOptions[i];
                if (option.Group != VendorCurrencyGroup.BicolorGemstones)
                {
                    continue;
                }

                if (option.CurrencyItemId != 0)
                {
                    return option.CurrencyItemId;
                }

                break;
            }
        }
        return entry.CurrencyOptions[0].CurrencyItemId;
    }

    private static void SortEntries(List<MaterialEntry> entries)
    {
        entries.Sort((left, right) =>
        {
            var leftReady = left.EffectiveAvailable >= left.Needed;
            var rightReady = right.EffectiveAvailable >= right.Needed;
            var readyComparison = leftReady.CompareTo(rightReady);
            if (readyComparison != 0)
            {
                return readyComparison;
            }

            return string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        });
    }

    private static Vector4 SourceColor(Vector4 hue)
        => M3.CustomColor(hue).Color;

    private void DrawMaterialPanel(MaterialPanel panel, float width, float height)
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        var accent = SourceColor(panel.Hue);

        ImGui.BeginGroup();
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();
        M3Card.Paint(drawList, min, max, accent, M3CardStyle.Filled, M3Draw.MouseOver(min, max, true));

        var innerWidth = MathF.Max(1f, width - M3Card.ContentInset - M3Card.RightInset);
        var x = min.X + M3Card.ContentInset;
        var y = min.Y + M3Card.TopPadding;

        float titleHeight;
        using (ImRaii.PushFont(M3.TitleMedium))
        {
            titleHeight = ImGui.CalcTextSize(panel.Label).Y;
        }

        var iconSize = M3Draw.MeasureIcon(panel.Icon);
        M3Draw.Icon(drawList, panel.Icon, new Vector2(x, y + (titleHeight - iconSize.Y) * 0.5f), accent);
        var titleX = x + iconSize.X + 8f * scale;

        var (count, countColor) = panel.Missing > 0
            ? ($"{panel.Missing} of {panel.Entries.Count} missing", s.Error)
            : ($"{panel.Entries.Count} ready", s.Success);
        var countSize = ImGui.CalcTextSize(count);
        var countX = MathF.Max(titleX, x + innerWidth - countSize.X);
        drawList.AddText(new Vector2(countX, y + (titleHeight - countSize.Y) * 0.5f), M3.U32(countColor, 0.95f), count);
        using (ImRaii.PushFont(M3.TitleMedium))
        {
            drawList.AddText(new Vector2(titleX, y), M3.U32(s.OnSurface),
                M3Navigation.Truncate(panel.Label, MathF.Max(8f * scale, countX - 8f * scale - titleX)));
        }

        y += titleHeight + M3.Space2;

        if (panel.ShowCurrencyTotals)
        {
            var totals = ComputeCurrencyTotals(panel.Entries, panel.PreferBicolorDefault, ignoreOwned: _matsKeepFulfilled);
            if (totals.Count > 0)
            {
                ImGui.SetCursorScreenPos(new Vector2(x, y));
                ImGui.BeginGroup();
                DrawCurrencyTotals(totals, innerWidth);
                ImGui.EndGroup();
                y = ImGui.GetItemRectMax().Y + M3.Space2;
            }
        }

        ImGui.SetCursorScreenPos(new Vector2(x, y));
        DrawMaterialTable(panel, new Vector2(innerWidth, MathF.Max(1f, max.Y - M3.Space2 - y)));

        ImGui.SetCursorScreenPos(min);
        ImGui.Dummy(new Vector2(width, height));
        ImGui.EndGroup();
    }

    private void DrawMaterialTable(MaterialPanel panel, Vector2 size)
    {
        var mode = panel.RetainerColumns;
        var columnCount = mode switch
        {
            RetainerColumnMode.Total => 5,
            RetainerColumnMode.Split => 6,
            _ => 4,
        };

        float headerWidth;
        using (ImRaii.PushFont(M3.LabelSmall))
        {
            headerWidth = ImGui.CalcTextSize("Need").X;
        }

        var numberWidth = MathF.Max(headerWidth, ImGui.CalcTextSize("9999").X) + 4f * M3.Scale;
        var barWidth = ImGui.CalcTextSize("100%").X + 14f * M3.Scale;
        const ImGuiTableFlags tableFlags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit
                                         | ImGuiTableFlags.PadOuterX;

        using var colors = M3Table.PushStyle();
        if (!ImGui.BeginTable($"{panel.Id}_tbl", columnCount, tableFlags, size))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, numberWidth);
        switch (mode)
        {
            case RetainerColumnMode.Total:
                ImGui.TableSetupColumn("Ret", ImGuiTableColumnFlags.WidthFixed, numberWidth);
                break;
            case RetainerColumnMode.Split:
                ImGui.TableSetupColumn("RNQ", ImGuiTableColumnFlags.WidthFixed, numberWidth);
                ImGui.TableSetupColumn("RHQ", ImGuiTableColumnFlags.WidthFixed, numberWidth);
                break;
        }

        ImGui.TableSetupColumn("Need", ImGuiTableColumnFlags.WidthFixed, numberWidth);
        ImGui.TableSetupColumn("%", ImGuiTableColumnFlags.WidthFixed, barWidth);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableNextColumn();
        M3Table.Header("Item", center: false);
        ImGui.TableNextColumn();
        M3Table.Header("Have", "In your inventory");
        switch (mode)
        {
            case RetainerColumnMode.Total:
                ImGui.TableNextColumn();
                M3Table.Header("Ret", "Retainer total (via Allagan Tools)");
                break;
            case RetainerColumnMode.Split:
                ImGui.TableNextColumn();
                M3Table.Header("RNQ", "Retainer NQ (via Allagan Tools)");
                ImGui.TableNextColumn();
                M3Table.Header("RHQ", "Retainer HQ (via Allagan Tools)");
                break;
        }

        ImGui.TableNextColumn();
        M3Table.Header("Need");
        ImGui.TableNextColumn();
        M3Table.Header("%", "How much of the need you already have");

        var entries = panel.Entries;
        var maxNameWidth = 0f;
        for (var i = 0; i < entries.Count; i++)
        {
            maxNameWidth = MathF.Max(maxNameWidth, ImGui.CalcTextSize(entries[i].Name).X);
        }

        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(entries.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                DrawPanelRow(entries[i], mode, panel.VendorTargets, maxNameWidth, panel.PreferBicolorDefault);
            }
        }

        clipper.End();
        clipper.Destroy();

        ImGui.EndTable();
    }

    private void DrawPanelRow(MaterialEntry entry, RetainerColumnMode mode,
        IReadOnlyList<VendorBuyListManager.VendorTargetRequest>? vendorTargets, float maxNameWidth, bool preferBicolorDefault)
    {
        var s = M3.Scheme;
        var satisfied = entry.EffectiveAvailable >= entry.Needed;
        var lineHeight = ImGui.GetTextLineHeight();

        using var rowId = ImRaii.PushId((int)(entry.ItemId * 2 + (entry.IsPrecraft ? 1u : 0u)));
        ImGui.TableNextRow();
        var rowTone = (satisfied, entry.IsPrecraft) switch
        {
            (true, false) => s.Success,
            (true, true) => s.Info,
            (false, false) => s.Error,
            (false, true) => s.Warning,
        };
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, M3.U32(rowTone, 0.10f));

        ImGui.TableNextColumn();
        M3Widgets.GameIcon(entry.IconId, lineHeight);
        ImGui.SameLine(0, MaterialRowIconSpacing);
        var nameTextStartX = ImGui.GetCursorScreenPos().X;
        if (ImGui.Selectable(entry.Name, false, ImGuiSelectableFlags.AllowItemOverlap))
        {
            CopyName(entry.Name);
        }

        DrawItemContextMenu(entry, vendorTargets);
        M3Tooltip.Hovered("Click to copy the name. Right-click for more.");

        var sourceIcons = GetVisibleSourceIcons(entry);
        if (sourceIcons.Count > 0 || entry.CurrencyOptions.Count > 0 || entry.DropInfo.HasData)
        {
            var iconColumnX = nameTextStartX + maxNameWidth + MaterialRowIconGutter;
            ImGui.SameLine(0, 0);
            var gap = iconColumnX - ImGui.GetCursorScreenPos().X;
            ImGui.Dummy(new Vector2(gap > 0f ? gap : MaterialRowIconSpacing, 0));
            ImGui.SameLine(0, 0);

            var hasDrawnIcon = false;
            if (sourceIcons.Count > 0)
            {
                CraftingRowIcons.DrawIconsRightAligned(sourceIcons, lineHeight, MaterialRowIconSpacing);
                hasDrawnIcon = true;
            }

            if (entry.CurrencyOptions.Count > 0)
            {
                if (hasDrawnIcon)
                {
                    ImGui.SameLine(0, MaterialRowIconSpacing);
                }

                hasDrawnIcon = DrawCurrencyPicker(entry, preferBicolorDefault, lineHeight);
            }

            if (entry.DropInfo.HasData)
            {
                DrawMobDropIcon(entry, hasDrawnIcon, lineHeight);
            }
        }

        ImGui.TableNextColumn();
        CenterNumber(entry.Have, satisfied ? s.Success : s.Error);
        switch (mode)
        {
            case RetainerColumnMode.Total:
                ImGui.TableNextColumn();
                CenterCount(entry.RetNQ + entry.RetHQ, s.OnSurface);
                break;
            case RetainerColumnMode.Split:
                ImGui.TableNextColumn();
                CenterCount(entry.RetNQ, s.OnSurface);
                ImGui.TableNextColumn();
                CenterCount(entry.RetHQ, s.Info);
                break;
        }

        ImGui.TableNextColumn();
        CenterNumber(entry.Needed, s.OnSurface);

        ImGui.TableNextColumn();
        DrawProgressCell(entry, satisfied, lineHeight);
    }

    private static void CopyName(string name)
    {
        try
        {
            ImGui.SetClipboardText(name);
            M3Snackbar.Show($"Copied {name}.");
        }
        catch
        {
        }
    }

    private static void CenterCount(int value, Vector4 color)
    {
        if (value > 0)
        {
            CenterNumber(value, color);
        }
        else
        {
            M3Table.CenteredText("—", M3Text.ColorOf(M3TextStyle.Faint));
        }
    }

    private static void CenterNumber(int value, Vector4 color)
    {
        M3Table.CenteredText(value > 9999 ? "9999" : value.ToString(), color);
        if (value > 9999)
        {
            M3Tooltip.Hovered(value.ToString("N0"));
        }
    }

    private void DrawProgressCell(MaterialEntry entry, bool satisfied, float height)
    {
        var ratio = entry.Needed > 0 ? (float)entry.EffectiveAvailable / entry.Needed : 1f;
        var progress = Math.Clamp(ratio, 0f, 1f);
        var text = $"{(_matsOvercapPercent ? ratio : progress) * 100f:F0}%";
        M3Table.Progress(progress, text, satisfied ? M3.Scheme.Success : M3.Scheme.Error, height);
    }

    private static void DrawItemContextMenu(MaterialEntry entry, IReadOnlyList<VendorBuyListManager.VendorTargetRequest>? vendorTargets)
    {
        using var menu = M3Menu.BeginContext("##material_menu");
        if (!menu.IsOpen)
        {
            return;
        }

        if (M3Menu.Item("##material_link", "Create link", FontAwesomeIcon.Link, tooltip: "Print the item's link in chat"))
        {
            Communicator.Print(SeString.CreateItemLink(entry.ItemId));
        }

        if (M3Menu.Item("##material_marketboard", "Search marketboard", FontAwesomeIcon.ChartLine))
        {
            GatherBuddy.MarketboardService?.QueueLookup(entry.ItemId, entry.Name, entry.IconId);
            GatherBuddy.VulcanWindow?.OpenToMarketboardItem(entry.ItemId);
        }

        DrawVendorBuyListMenu(entry, vendorTargets);
    }

    private static void DrawVendorBuyListMenu(MaterialEntry entry, IReadOnlyList<VendorBuyListManager.VendorTargetRequest>? vendorTargets)
    {
        if (GatherBuddy.VendorBuyListManager == null)
        {
            return;
        }

        var hasSingleTarget = TryCreateVendorBuyListTarget(entry, out var singleTarget);
        var hasBatchTargets = vendorTargets is { Count: > 1 };
        if (!hasSingleTarget && !hasBatchTargets)
        {
            return;
        }

        M3Menu.Divider();
        M3Menu.Heading("Vendor buy lists");
        if (hasSingleTarget)
        {
            DrawVendorBuyListExistingListMenu("##material_add_existing", "Add to existing list", [singleTarget]);
            if (M3Menu.Item("##material_new_list", "Create new list", FontAwesomeIcon.Plus))
            {
                OpenCreateVendorBuyListPopup([singleTarget]);
            }
        }

        if (hasBatchTargets)
        {
            DrawVendorBuyListExistingListMenu("##material_add_all_existing", "Add all missing to existing list", vendorTargets!);
            if (M3Menu.Item("##material_new_list_all", "Create new list with all missing", FontAwesomeIcon.Plus,
                    tooltip: "Every missing item in this panel that a vendor sells"))
            {
                OpenCreateVendorBuyListPopup(vendorTargets!);
            }
        }
    }

    private static void DrawVendorBuyListExistingListMenu(string id, string label, IReadOnlyList<VendorBuyListManager.VendorTargetRequest> targets)
    {
        var buyListManager = GatherBuddy.VendorBuyListManager;
        if (buyListManager == null)
        {
            return;
        }

        using var sub = M3Menu.BeginSub(id, label, FontAwesomeIcon.ListUl, VendorBuyListManager.Lists.Count > 0);
        if (!sub.IsOpen)
        {
            return;
        }

        foreach (var list in VendorBuyListManager.GetListsByCreation(true))
        {
            if (M3Menu.Item($"##vendor_list_{list.Id}", list.Name))
            {
                AddTargetsToVendorBuyList(list.Id, list.Name, targets);
            }
        }
    }

    private static void AddTargetsToVendorBuyList(Guid listId, string listName, IReadOnlyList<VendorBuyListManager.VendorTargetRequest> targets)
    {
        var buyListManager = GatherBuddy.VendorBuyListManager;
        if (buyListManager == null)
        {
            return;
        }

        var addedCount = buyListManager.TrySetTargets(listId, targets, selectList: true, openWindow: true, announce: true);
        if (addedCount == 0)
        {
            GatherBuddy.Log.Debug($"[CraftingMaterialsWindow] Unable to add {targets.Count:N0} vendor target(s) to vendor buy list '{listName}'.");
        }
    }

    private static void OpenCreateVendorBuyListPopup(IReadOnlyList<VendorBuyListManager.VendorTargetRequest> targets)
    {
        var vendorBuyListWindow = GatherBuddy.VendorBuyListWindow;
        if (vendorBuyListWindow == null)
        {
            GatherBuddy.Log.Warning("[CraftingMaterialsWindow] Unable to open Create Vendor List popup: vendor buy list window unavailable.");
            return;
        }

        if (!vendorBuyListWindow.OpenCreateListPopup(targets))
        {
            GatherBuddy.Log.Debug($"[CraftingMaterialsWindow] Unable to create a new vendor buy list for {targets.Count:N0} vendor target(s).");
        }
    }

    private static List<VendorBuyListManager.VendorTargetRequest> BuildVendorBuyListTargets(IEnumerable<MaterialEntry> entries)
    {
        var targets = new List<VendorBuyListManager.VendorTargetRequest>();
        var targetIndexes = new Dictionary<uint, int>();
        foreach (var entry in entries)
        {
            if (!TryCreateVendorBuyListTarget(entry, out var target) || target.ItemId == 0 || target.TargetQuantity == 0)
            {
                continue;
            }

            if (!targetIndexes.TryGetValue(target.ItemId, out var targetIndex))
            {
                targetIndexes.Add(target.ItemId, targets.Count);
                targets.Add(target);
            }
            else if (target.TargetQuantity > targets[targetIndex].TargetQuantity)
            {
                targets[targetIndex] = target;
            }
        }

        return targets;
    }

    private static bool TryCreateVendorBuyListTarget(MaterialEntry entry, out VendorBuyListManager.VendorTargetRequest target)
    {
        target = default;
        var buyListManager = GatherBuddy.VendorBuyListManager;
        if (buyListManager == null || !VendorBuyListManager.CanAddSupportedItem(entry.ItemId))
        {
            return false;
        }

        var missingQuantity = entry.Needed - entry.EffectiveAvailable;
        if (missingQuantity <= 0)
        {
            return false;
        }

        var currentCount = (uint)Math.Max(0, VendorBuyListManager.GetCurrentInventoryAndArmoryCount(entry.ItemId));
        target = new VendorBuyListManager.VendorTargetRequest(entry.ItemId, currentCount + (uint)missingQuantity);
        return true;
    }

    private List<CurrencyTotal> ComputeCurrencyTotals(IReadOnlyList<MaterialEntry> entries, bool preferBicolorDefault, bool ignoreOwned)
    {
        var totals = new Dictionary<uint, (long Amount, string Name, ushort IconId)>();
        foreach (var entry in entries)
        {
            var quantity = ignoreOwned
                ? entry.Needed
                : entry.Needed - entry.EffectiveAvailable;
            if (quantity <= 0)
            {
                continue;
            }

            var selectedCurrencyId = GetSelectedCurrencyItemId(entry, preferBicolorDefault);
            if (selectedCurrencyId == 0)
            {
                continue;
            }

            var option = entry.CurrencyOptions[IndexOfCurrencyOption(entry.CurrencyOptions, selectedCurrencyId)];
            var totalCost = (long)option.CostPerItem * quantity;
            if (totals.TryGetValue(option.CurrencyItemId, out var existing))
            {
                totals[option.CurrencyItemId] = (existing.Amount + totalCost, existing.Name, existing.IconId);
            }
            else
            {
                totals[option.CurrencyItemId] = (totalCost, option.Name, option.IconId);
            }
        }
        var sortedTotals = new List<(CurrencyTotal Total, int Index)>(totals.Count);
        foreach (var (currencyItemId, total) in totals)
        {
            sortedTotals.Add((new CurrencyTotal(currencyItemId, total.Amount, total.Name, total.IconId), sortedTotals.Count));
        }

        sortedTotals.Sort(static (lhs, rhs) =>
        {
            var order = rhs.Total.Amount.CompareTo(lhs.Total.Amount);
            return order != 0 ? order : lhs.Index.CompareTo(rhs.Index);
        });
        return sortedTotals.ConvertAll(static entry => entry.Total);
    }

    private static ushort ResolveCurrencyIconId(uint currencyItemId)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        if (sheet != null && sheet.TryGetRow(currencyItemId, out var item))
        {
            return (ushort)item.Icon;
        }

        return 0;
    }

    private static void DrawCurrencyTotals(IReadOnlyList<CurrencyTotal> totals, float width)
    {
        var flow = new M3Flow(M3.Space1, width);
        for (var i = 0; i < totals.Count; i++)
        {
            var total = totals[i];
            var label = total.Amount.ToString("N0");
            var size = CurrencyPillSize(label, total.IconId != 0);
            flow.Next(size.X);
            DrawCurrencyPill(total, label, size);
        }
    }

    private static Vector2 CurrencyPillSize(string label, bool hasIcon)
    {
        var scale = M3.Scale;
        var height = M3.FitText(24f, 3f);
        var width = ImGui.CalcTextSize(label).X + 18f * scale;
        if (hasIcon)
        {
            width += height - 6f * scale + 6f * scale;
        }

        return new Vector2(width, height);
    }

    private static void DrawCurrencyPill(CurrencyTotal total, string label, Vector2 size)
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        ImGui.Dummy(size);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var rounding = size.Y * 0.5f;
        drawList.AddRectFilled(min, max, M3.U32(s.SurfaceContainerHighest, 0.7f), rounding);
        drawList.AddRect(min, max, M3.U32(s.OutlineVariant, 0.6f), rounding, ImDrawFlags.None, 1f * scale);

        var x = min.X + 8f * scale;
        if (total.IconId != 0)
        {
            var iconSize = size.Y - 6f * scale;
            x = min.X + 4f * scale;
            var wrap = M3ActionIcon.GameIcon(total.IconId);
            if (wrap != null)
            {
                M3Draw.Texture(drawList, wrap, new Vector2(x, min.Y + 3f * scale), iconSize);
            }

            x += iconSize + 6f * scale;
        }

        var textSize = ImGui.CalcTextSize(label);
        drawList.AddText(new Vector2(x, min.Y + (size.Y - textSize.Y) * 0.5f), M3.U32(s.OnSurface), label);

        if (!string.IsNullOrEmpty(total.Name) && ImGui.IsItemHovered())
        {
            M3Tooltip.Show(total.Name);
        }
    }

    private static IReadOnlyList<CraftingRowIcons.RowIcon> GetVisibleSourceIcons(MaterialEntry entry)
    {
        var sourceIcons = CraftingRowIcons.GetMaterialIcons(entry.ItemId, entry.IsPrecraft);
        if (entry.CurrencyOptions.Count == 0 || sourceIcons.Count == 0)
        {
            return sourceIcons;
        }

        var currencyIconIds = new HashSet<uint>();
        foreach (var option in entry.CurrencyOptions)
        {
            currencyIconIds.Add(option.IconId);
        }

        var filtered = new List<CraftingRowIcons.RowIcon>(sourceIcons.Count);
        foreach (var sourceIcon in sourceIcons)
        {
            if (!currencyIconIds.Contains(sourceIcon.IconId))
            {
                filtered.Add(sourceIcon);
            }
        }

        return filtered;
    }

    private bool DrawCurrencyPicker(MaterialEntry entry, bool preferBicolorDefault, float size)
    {
        if (entry.CurrencyOptions.Count == 0)
        {
            return false;
        }

        var selectedCurrencyId = GetSelectedCurrencyItemId(entry, preferBicolorDefault);
        var choosable = entry.CurrencyOptions.Count > 1;
        for (var i = 0; i < entry.CurrencyOptions.Count; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(0, MaterialRowIconSpacing);
            }

            var option = entry.CurrencyOptions[i];
            var isSelected = option.CurrencyItemId == selectedCurrencyId;
            var clicked = M3Widgets.GameIconButton($"##currency_{option.CurrencyItemId}", option.IconId, size, isSelected ? 1f : 0.35f);
            if (ImGui.IsItemHovered())
            {
                M3Tooltip.Show(!choosable
                    ? $"{option.Name}: {option.CostPerItem:N0} each"
                    : isSelected
                        ? $"{option.Name}: {option.CostPerItem:N0} each\nUsed for the totals."
                        : $"{option.Name}: {option.CostPerItem:N0} each\nClick to use it for the totals.");
            }

            if (clicked)
            {
                _userSelectedCurrencyByItem[entry.ItemId] = option.CurrencyItemId;
            }
        }

        return true;
    }

    private static void DrawMobDropIcon(MaterialEntry entry, bool addLeadingSpacing, float size)
    {
        const string popupId = "##mob_drops";
        if (addLeadingSpacing)
        {
            ImGui.SameLine(0, MaterialRowIconSpacing);
        }

        var clicked = M3Widgets.GameIconButton("##mob_drops_button", MobDropIconId, size);
        if (ImGui.IsItemHovered() && !ImGui.IsPopupOpen(popupId))
        {
            M3Tooltip.Show(BuildMobDropTooltip(entry.DropInfo));
        }

        if (clicked)
        {
            ImGui.OpenPopup(popupId);
        }

        DrawMobDropPopup(entry, popupId);
    }

    private static string BuildMobDropTooltip(MobDropItemInfo dropInfo)
    {
        const int MaxMobsShown = 6;
        var lines = new List<string>(MaxMobsShown + 3)
        {
            $"Drops from {BuildMobDropSummaryText(dropInfo)}.",
        };
        for (var i = 0; i < dropInfo.Mobs.Count && i < MaxMobsShown; i++)
        {
            var mob = dropInfo.Mobs[i];
            var mobDisplayName = NormalizeMobDisplayName(mob.MobName);
            var zoneSummary = mob.ZoneCount == 1
                ? mob.Zones[0].ZoneName
                : $"{mob.ZoneCount} {Pluralize("zone", mob.ZoneCount)}";
            lines.Add($"{mobDisplayName} — {zoneSummary} · {mob.ClusterCount} {Pluralize("area", mob.ClusterCount)}");
        }

        if (dropInfo.MobCount > MaxMobsShown)
        {
            var remainingMobCount = dropInfo.MobCount - MaxMobsShown;
            lines.Add($"...and {remainingMobCount} more {Pluralize("mob", remainingMobCount)}");
        }

        lines.Add("Click for map flags and teleports.");
        return string.Join('\n', lines);
    }

    private static void DrawMobDropPopup(MaterialEntry entry, string popupId)
    {
        var width = VulcanUiScaling.Scaled(440f);
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0f), new Vector2(width, VulcanUiScaling.Scaled(480f)));
        var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(14f, 12f) * M3.PaddingScale);
        var background = ImRaii.PushColor(ImGuiCol.PopupBg, M3.Scheme.SurfaceContainer);
        var open = ImGui.BeginPopup(popupId);
        background.Dispose();
        padding.Dispose();
        if (!open)
        {
            return;
        }

        try
        {
            DrawMobDropPopupContents(entry);
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private static void DrawMobDropPopupContents(MaterialEntry entry)
    {
        var scale = M3.Scale;
        using (ImRaii.PushFont(M3.TitleMedium))
        {
            ImGui.TextUnformatted($"Drops for {entry.Name}");
        }

        M3Text.Draw(BuildMobDropSummaryText(entry.DropInfo), M3TextStyle.Muted);
        M3Widgets.Divider(4f);

        var zoneGroups = new List<(uint TerritoryTypeId, string ZoneName, List<(MobDropMobInfo Mob, MobDropZoneInfo Zone)> Entries, int Index)>();
        var zoneIndexes = new Dictionary<(uint, string), int>();
        foreach (var mob in entry.DropInfo.Mobs)
        {
            foreach (var zone in mob.Zones)
            {
                if (!zoneIndexes.TryGetValue((zone.TerritoryTypeId, zone.ZoneName), out var zoneIndex))
                {
                    zoneIndex = zoneGroups.Count;
                    zoneIndexes.Add((zone.TerritoryTypeId, zone.ZoneName), zoneIndex);
                    zoneGroups.Add((zone.TerritoryTypeId, zone.ZoneName, [], zoneIndex));
                }

                zoneGroups[zoneIndex].Entries.Add((mob, zone));
            }
        }

        zoneGroups.Sort(static (lhs, rhs) =>
        {
            var order = StringComparer.OrdinalIgnoreCase.Compare(lhs.ZoneName, rhs.ZoneName);
            return order != 0 ? order : lhs.Index.CompareTo(rhs.Index);
        });
        var isAppearing = ImGui.IsWindowAppearing();
        var defaultZoneOpen = entry.DropInfo.ZoneCount == 1;
        var buttonSize = MathF.Max(ImGui.GetFrameHeight(), 26f * scale);
        var flagTint = SourceColor(HueMapMarker);
        var teleportTint = SourceColor(HueTeleport);
        var zoneTint = SourceColor(HueDrop);
        var childIndent = 26f * scale;

        for (var i = 0; i < zoneGroups.Count; i++)
        {
            var zoneGroup = zoneGroups[i];
            var sortedZoneEntries = new List<(string MobName, (MobDropMobInfo Mob, MobDropZoneInfo Zone) Entry, int Index)>(zoneGroup.Entries.Count);
            for (var entryIndex = 0; entryIndex < zoneGroup.Entries.Count; entryIndex++)
            {
                var zoneEntry = zoneGroup.Entries[entryIndex];
                sortedZoneEntries.Add((NormalizeMobDisplayName(zoneEntry.Mob.MobName), zoneEntry, entryIndex));
            }

            sortedZoneEntries.Sort(static (lhs, rhs) =>
            {
                var order = StringComparer.OrdinalIgnoreCase.Compare(lhs.MobName, rhs.MobName);
                return order != 0 ? order : lhs.Index.CompareTo(rhs.Index);
            });
            var zoneEntries = sortedZoneEntries.ConvertAll(static sorted => sorted.Entry);
            var zoneStateId = $"{entry.ItemId}_{(entry.IsPrecraft ? 1 : 0)}_{zoneGroup.TerritoryTypeId}_{i}";
            if (isAppearing && defaultZoneOpen)
            {
                MobDropZoneOpenStates[zoneStateId] = true;
            }

            var zoneOpen = MobDropZoneOpenStates.TryGetValue(zoneStateId, out var known) ? known : defaultZoneOpen;

            using var zoneId = ImRaii.PushId(i);
            var buttonsWidth = buttonSize * 2f;
            var headerWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X - buttonsWidth - M3.Space1);
            var mobCount = $"{zoneEntries.Count} {Pluralize("mob", zoneEntries.Count)}";
            if (DrawZoneHeader("##zone", zoneGroup.ZoneName, mobCount, zoneOpen, zoneTint, new Vector2(headerWidth, buttonSize)))
            {
                zoneOpen = !zoneOpen;
            }

            MobDropZoneOpenStates[zoneStateId] = zoneOpen;

            ImGui.SameLine(0, M3.Space1);
            var zonePrimaryCluster = GetZonePrimaryCluster(zoneEntries);
            if (zonePrimaryCluster.HasValue)
            {
                DrawMobDropFlagButton(zonePrimaryCluster.Value.Cluster, zonePrimaryCluster.Value.MobName, "##flag_zone",
                    "Place a map marker near the center of this zone's mob locations.",
                    "No zone-level coordinates are available for this zone.", flagTint, buttonSize);
                ImGui.SameLine(0, 0);
                DrawMobDropTeleportButton(zonePrimaryCluster.Value.Cluster, zonePrimaryCluster.Value.MobName, "##teleport_zone",
                    "Teleport to the nearest aetheryte for this zone and place a map marker there.",
                    "No teleport target available for this zone.", teleportTint, buttonSize);
            }
            else
            {
                M3Widgets.IconButton("##flag_zone", FontAwesomeIcon.MapMarkerAlt, "No zone-level coordinates are available for this zone.",
                    diameter: buttonSize, enabled: false);
                ImGui.SameLine(0, 0);
                M3Widgets.IconButton("##teleport_zone", FontAwesomeIcon.Running, "No teleport target available for this zone.",
                    diameter: buttonSize, enabled: false);
            }

            if (zoneOpen)
            {
                ImGui.Indent(childIndent);
                for (var zoneEntryIndex = 0; zoneEntryIndex < zoneEntries.Count; zoneEntryIndex++)
                {
                    var (mob, zone) = zoneEntries[zoneEntryIndex];
                    var mobDisplayName = NormalizeMobDisplayName(mob.MobName);

                    using var mobId = ImRaii.PushId($"{mob.BNpcNameId}_{zone.TerritoryTypeId}");
                    ImGui.Dummy(new Vector2(0f, 2f * scale));
                    ImGui.TextUnformatted(mobDisplayName);

                    for (var clusterIndex = 0; clusterIndex < zone.Clusters.Count; clusterIndex++)
                    {
                        var cluster = zone.Clusters[clusterIndex];
                        using var clusterId = ImRaii.PushId(clusterIndex);

                        DrawMobDropFlagButton(cluster, mobDisplayName, "##flag",
                            "Place a map marker for this location.",
                            "No coordinates available for this location.", flagTint, buttonSize);
                        ImGui.SameLine(0, 0);
                        DrawMobDropTeleportButton(cluster, mobDisplayName, "##teleport",
                            "Teleport to the nearest aetheryte for this location and place a map marker there.",
                            "No teleport target available for this location.", teleportTint, buttonSize);
                        ImGui.SameLine(0, M3.Space2);
                        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (buttonSize - ImGui.GetTextLineHeight()) * 0.5f);
                        M3Text.Draw(BuildMobDropClusterText(cluster), cluster.HasCoordinates ? M3TextStyle.Muted : M3TextStyle.Faint);
                    }
                }

                ImGui.Unindent(childIndent);
            }

            if (i < zoneGroups.Count - 1)
            {
                M3Widgets.Divider(4f);
            }
        }
    }

    private static bool DrawZoneHeader(string id, string zoneName, string trailing, bool open, Vector4 tint, Vector2 size)
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        var pressed = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var held = ImGui.IsItemActive();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();

        if (hovered || held)
        {
            drawList.AddRectFilled(min, max, M3.U32(s.OnSurface, held ? M3.StatePressed : M3.StateHover), M3.ShapeSmall);
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        var chevronSlot = 20f * scale;
        M3Draw.Chevron(drawList, new Vector2(min.X + chevronSlot * 0.5f + 2f * scale, (min.Y + max.Y) * 0.5f), open ? 1f : 0f,
            M3.Alpha(s.OnSurfaceVariant, hovered ? 1f : 0.8f), chevronSlot * 0.22f);

        var x = min.X + chevronSlot + 6f * scale;
        var iconSize = M3Draw.MeasureIcon(FontAwesomeIcon.Map);
        M3Draw.Icon(drawList, FontAwesomeIcon.Map, new Vector2(x, min.Y + (size.Y - iconSize.Y) * 0.5f), tint);
        x += iconSize.X + 8f * scale;

        var trailingSize = ImGui.CalcTextSize(trailing);
        var label = M3Navigation.Truncate(zoneName, MathF.Max(8f * scale, max.X - x - trailingSize.X - 16f * scale));
        var labelSize = ImGui.CalcTextSize(label);
        drawList.AddText(new Vector2(x, min.Y + (size.Y - labelSize.Y) * 0.5f), M3.U32(s.OnSurface), label);
        drawList.AddText(new Vector2(max.X - 8f * scale - trailingSize.X, min.Y + (size.Y - trailingSize.Y) * 0.5f),
            M3.U32(s.OnSurfaceVariant, 0.85f), trailing);

        if (hovered && label != zoneName)
        {
            M3Tooltip.Show(zoneName);
        }

        return pressed;
    }

    private static string BuildMobDropSummaryText(MobDropItemInfo dropInfo)
        => $"{dropInfo.MobCount} {Pluralize("mob", dropInfo.MobCount)} · {dropInfo.ZoneCount} {Pluralize("zone", dropInfo.ZoneCount)} · {dropInfo.ClusterCount} {Pluralize("area", dropInfo.ClusterCount)}";

    private static string BuildMobDropClusterText(MobDropClusterInfo cluster)
        => cluster.HasCoordinates
            ? $"{cluster.MapX:F1}, {cluster.MapY:F1}"
            : "No coordinates";

    private static (MobDropClusterInfo Cluster, string MobName)? GetZonePrimaryCluster(
        IReadOnlyList<(MobDropMobInfo Mob, MobDropZoneInfo Zone)> zoneEntries)
    {
        var candidates = new List<(MobDropClusterInfo Cluster, string MobName)>();
        foreach (var (mob, zone) in zoneEntries)
        {
            foreach (var cluster in zone.Clusters)
            {
                if (cluster.HasCoordinates)
                {
                    candidates.Add((cluster, NormalizeMobDisplayName(mob.MobName)));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var bestCandidate = candidates[0];
        var bestScore = double.PositiveInfinity;
        for (var i = 0; i < candidates.Count; i++)
        {
            var score = 0d;
            for (var j = 0; j < candidates.Count; j++)
            {
                var weight = Math.Max(1, candidates[j].Cluster.SpawnPointCount);
                var deltaX = candidates[i].Cluster.MapX - candidates[j].Cluster.MapX;
                var deltaY = candidates[i].Cluster.MapY - candidates[j].Cluster.MapY;
                score += weight * (deltaX * deltaX + deltaY * deltaY);
            }

            if (score > bestScore)
            {
                continue;
            }

            if (Math.Abs(score - bestScore) < 0.0001d
             && candidates[i].Cluster.SpawnPointCount < bestCandidate.Cluster.SpawnPointCount)
            {
                continue;
            }

            bestScore = score;
            bestCandidate = candidates[i];
        }

        return bestCandidate;
    }

    private static string NormalizeMobDisplayName(string mobName)
    {
        if (string.IsNullOrWhiteSpace(mobName))
        {
            return mobName;
        }

        var chars = mobName.ToLowerInvariant().ToCharArray();
        var capitalizeNext = true;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (char.IsLetter(c))
            {
                if (capitalizeNext)
                {
                    chars[i] = char.ToUpperInvariant(c);
                }

                capitalizeNext = false;
                continue;
            }

            if (char.IsDigit(c))
            {
                capitalizeNext = false;
                continue;
            }

            capitalizeNext = c != '\'';
        }

        return new string(chars);
    }

    private static string Pluralize(string singular, int count)
        => count == 1 ? singular : $"{singular}s";

    private static void DrawMobDropFlagButton(MobDropClusterInfo cluster, string mobName, string id, string tooltip, string unavailableTooltip,
        Vector4 tint, float size)
    {
        if (!cluster.HasCoordinates)
        {
            M3Widgets.IconButton(id, FontAwesomeIcon.MapMarkerAlt, unavailableTooltip, diameter: size, enabled: false);
            return;
        }

        if (M3Widgets.IconButton(id, FontAwesomeIcon.MapMarkerAlt, tooltip, tint: tint, diameter: size))
        {
            PlaceMobDropFlag(cluster, mobName);
        }
    }

    private static void DrawMobDropTeleportButton(MobDropClusterInfo cluster, string mobName, string id, string tooltip, string unavailableTooltip,
        Vector4 tint, float size)
    {
        var aetheryte = ResolveMobDropAetheryte(cluster);
        if (aetheryte == null)
        {
            M3Widgets.IconButton(id, FontAwesomeIcon.Running, unavailableTooltip, diameter: size, enabled: false);
            return;
        }

        if (M3Widgets.IconButton(id, FontAwesomeIcon.Running, tooltip, tint: tint, diameter: size))
        {
            TeleportToMobDropAetheryte(cluster, mobName, aetheryte);
        }
    }

    private static global::GatherBuddy.Classes.Aetheryte? ResolveMobDropAetheryte(MobDropClusterInfo cluster)
    {
        if (!cluster.HasCoordinates || cluster.TerritoryTypeId == 0)
        {
            return null;
        }

        if (!GatherBuddy.GameData.Territories.TryGetValue(cluster.TerritoryTypeId, out var territory))
        {
            var territorySheet = Dalamud.GameData.GetExcelSheet<TerritoryType>();
            if (territorySheet == null || !territorySheet.TryGetRow(cluster.TerritoryTypeId, out var territoryRow))
            {
                return null;
            }

            territory = GatherBuddy.GameData.FindOrAddTerritory(territoryRow);
        }

        if (territory == null || territory.Aetherytes.Count == 0)
        {
            return null;
        }

        var mapX = (int)MathF.Round(cluster.MapX * 100f);
        var mapY = (int)MathF.Round(cluster.MapY * 100f);
        global::GatherBuddy.Classes.Aetheryte? closest = null;
        var closestDistance = 0;
        foreach (var aetheryte in territory.Aetherytes)
        {
            var distance = aetheryte.WorldDistance(territory.Id, mapX, mapY);
            if (closest == null || distance < closestDistance || distance == closestDistance && aetheryte.Id < closest.Id)
            {
                closest = aetheryte;
                closestDistance = distance;
            }
        }

        return closest;
    }

    private static void TeleportToMobDropAetheryte(MobDropClusterInfo cluster, string mobName, global::GatherBuddy.Classes.Aetheryte aetheryte)
    {
        try
        {
            PlaceMobDropFlag(cluster, mobName);
            Executor.TeleportToAetheryte(aetheryte);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingMaterialsWindow] Failed to teleport for {mobName} area {cluster.AreaIndex}: {ex.Message}");
        }
    }

    private static void PlaceMobDropFlag(MobDropClusterInfo cluster, string mobName)
    {
        try
        {
            var payload = new MapLinkPayload(cluster.TerritoryTypeId, cluster.MapRowId, cluster.MapX, cluster.MapY);
            Dalamud.GameGui.OpenMapWithMapLink(payload);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[CraftingMaterialsWindow] Failed to place map flag for {mobName} area {cluster.AreaIndex}: {ex.Message}");
        }
    }

}
