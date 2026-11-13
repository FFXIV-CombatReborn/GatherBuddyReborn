using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public class CraftingTreeWindow : M3PanelWindow
{
    public const string WindowId = "Crafting Tree###CraftingTree";
    private const int MaxRecursionDepth = 24;
    private const int MaxVendorsShown = 6;

    private static readonly Vector2 DefaultSize = new(600f, 620f);

    private static readonly WindowSizeConstraints DefaultConstraints = new()
    {
        MinimumSize = new Vector2(400f, 280f),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };

    private static readonly M3WindowAction[] TreeWindowActions =
    [
        new("##tree_materials", FontAwesomeIcon.Boxes, "Show the materials for this list"),
        new("##tree_vulcan", FontAwesomeIcon.Hammer, "Open Vulcan's crafting lists"),
    ];

    private static readonly Vector4 HueRoot = new(0.95f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 HueSubCraft = new(0.45f, 0.90f, 0.95f, 1f);
    private static readonly Vector4 HueGather = new(0.55f, 1.00f, 0.55f, 1f);
    private static readonly Vector4 HueFish = new(0.55f, 0.85f, 1.00f, 1f);
    private static readonly Vector4 HueVendor = new(1.00f, 0.85f, 0.30f, 1f);
    private static readonly Vector4 HueScrip = new(0.85f, 0.55f, 1.00f, 1f);
    private static readonly Vector4 HueDrop = new(1.00f, 0.55f, 0.55f, 1f);

    private static readonly string[] TagLabels = ["Final", "Craft", "Gather", "Fish", "Vendor", "Currency", "Scrip", "Drop", "Other"];

    private static float IndentPerLevel
        => VulcanUiScaling.Scaled(22f);

    private static float IconColumnPadding
        => VulcanUiScaling.Scaled(12f);

    private static float IconOverflowGap
        => VulcanUiScaling.Scaled(6f);

    private CraftingListEditor? _editor;
    private string? _windowNameListName;

    private List<TreeNode> _cachedTree = [];
    private long _cachedVersion = -1;
    private CraftingListEditor? _cachedEditor;
    private bool _cachedMobDropInfoInitialized;
    private string _cachedSummary = string.Empty;

    private readonly List<bool> _ancestorHasMoreSiblings = [];
    private float _originX;
    private float _visibleWidth;
    private float _rowHeight;
    private float _iconSize;
    private float _gap;
    private Vector2 _tagSize;
    private float _iconColumnScreenX;
    private Vector2 _itemSpacing;

    public CraftingTreeWindow()
        : base(WindowId, "Crafting Tree", DefaultSize, DefaultConstraints)
    {
        BlocksGameInput = false;
    }

    public void SetEditor(CraftingListEditor? editor)
    {
        if (!ReferenceEquals(_editor, editor))
        {
            InvalidateTree();
        }

        _editor = editor;
    }

    protected override bool BodyScrolls
        => false;

    protected override M3WindowAction[] WindowActions
        => TreeWindowActions;

    protected override void OnWindowAction(int index)
    {
        switch (index)
        {
            case 0:
                {
                    var materials = GatherBuddy.CraftingMaterialsWindow;
                    if (materials != null)
                    {
                        materials.SetEditor(_editor);
                        materials.OpenAndRestore();
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

            return _cachedSummary.Length == 0
                ? (_editor.ListName, null)
                : ($"{_editor.ListName} · {_cachedSummary}", null);
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
        WindowName = listName == null ? WindowId : $"Crafting Tree — {listName}###CraftingTree";
    }

    protected override void DrawContents()
    {
        if (_editor == null)
        {
            if (M3Widgets.EmptyState("##tree_no_list", "No list open", "Open a crafting list in Vulcan to see how its recipes break down.",
                    FontAwesomeIcon.Sitemap, "Open Vulcan"))
            {
                GatherBuddy.VulcanWindow?.OpenToCraftingLists();
            }

            return;
        }

        MobDropInfoCache.EnsureInitializeStarted();

        if (ShouldRebuild())
        {
            RebuildTree();
        }

        if (_cachedTree.Count == 0)
        {
            M3Widgets.EmptyState("##tree_empty", "Nothing to show", "This list is empty, or its recipes couldn't be resolved.",
                FontAwesomeIcon.Sitemap);
            return;
        }

        DrawToolbar();
        DrawTree();
    }

    private void DrawToolbar()
    {
        const string expandLabel = "Expand all";
        const string collapseLabel = "Collapse all";
        var flow = new M3Flow();
        flow.Next(M3Widgets.ChipWidth(expandLabel, FontAwesomeIcon.AngleDoubleDown));
        if (M3Widgets.Chip("##tree_expand", expandLabel, false, FontAwesomeIcon.AngleDoubleDown, "Show every ingredient"))
        {
            SetExpandedAll(true);
        }

        flow.Next(M3Widgets.ChipWidth(collapseLabel, FontAwesomeIcon.AngleDoubleUp));
        if (M3Widgets.Chip("##tree_collapse", collapseLabel, false, FontAwesomeIcon.AngleDoubleUp, "Show only the final items"))
        {
            SetExpandedAll(false);
        }

        ImGui.Dummy(new Vector2(0f, M3.Space1));
    }

    private void DrawTree()
    {
        using var body = ImRaii.Child("##craftingTreeBody", Vector2.Zero, false, ImGuiWindowFlags.HorizontalScrollbar);
        if (!body)
        {
            return;
        }

        _itemSpacing = ImGui.GetStyle().ItemSpacing;
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, _itemSpacing with { Y = 0f });
        var scale = M3.Scale;
        var lineHeight = ImGui.GetTextLineHeight();
        _rowHeight = M3.FitText(28f, 3f);
        _iconSize = MathF.Min(_rowHeight - 4f * scale, lineHeight + 2f * scale);
        _gap = 6f * scale;
        _tagSize = Vector2.Zero;
        foreach (var label in TagLabels)
        {
            _tagSize = Vector2.Max(_tagSize, M3Badge.Size(label));
        }

        _originX = ImGui.GetCursorScreenPos().X;
        _visibleWidth = ImGui.GetContentRegionAvail().X;
        _iconColumnScreenX = _originX + ComputeMaxLabelEnd() + IconColumnPadding;
        _ancestorHasMoreSiblings.Clear();
        for (var i = 0; i < _cachedTree.Count; i++)
        {
            DrawTreeNode(_cachedTree[i], 0, i == _cachedTree.Count - 1);
        }
    }

    private float LabelOffset
        => IndentPerLevel + _iconSize + _gap + _tagSize.X + _gap;

    private static float LabelWidth(TreeNode node)
        => ImGui.CalcTextSize(node.ItemName).X + (node.QuantityText.Length > 0 ? ImGui.CalcTextSize(node.QuantityText).X : 0f);

    private float ComputeMaxLabelEnd()
    {
        var max = 0f;
        foreach (var root in _cachedTree)
        {
            WalkForMaxLabelEnd(root, 0, ref max);
        }

        return max;
    }

    private void WalkForMaxLabelEnd(TreeNode node, int depth, ref float max)
    {
        max = MathF.Max(max, depth * IndentPerLevel + LabelOffset + LabelWidth(node));
        if (!node.Expanded)
        {
            return;
        }

        foreach (var child in node.Children)
        {
            WalkForMaxLabelEnd(child, depth + 1, ref max);
        }
    }

    private void SetExpandedAll(bool expanded)
    {
        foreach (var root in _cachedTree)
        {
            SetExpandedRecursive(root, expanded);
        }
    }

    private static void SetExpandedRecursive(TreeNode node, bool expanded)
    {
        node.Expanded = expanded;
        foreach (var child in node.Children)
        {
            SetExpandedRecursive(child, expanded);
        }
    }

    private static int SumCrafts(IEnumerable<TreeNode> nodes)
    {
        var total = 0;
        foreach (var node in nodes)
        {
            if (node.IsCraft && !node.IsRoot)
            {
                total++;
            }

            total += SumCrafts(node.Children);
        }
        return total;
    }

    private static int SumLeaves(IEnumerable<TreeNode> nodes, params MaterialSource[] sources)
    {
        var total = 0;
        foreach (var node in nodes)
        {
            if (!node.IsCraft && node.MaterialSource is { } src && Array.IndexOf(sources, src) >= 0)
            {
                total++;
            }

            total += SumLeaves(node.Children, sources);
        }
        return total;
    }

    private static string BuildSummary(List<TreeNode> tree)
    {
        var crafts = SumCrafts(tree);
        var gather = SumLeaves(tree, MaterialSource.Gatherable, MaterialSource.Fish);
        var vendor = SumLeaves(tree, MaterialSource.GilVendor, MaterialSource.SpecialCurrency, MaterialSource.Scrip);
        return $"{tree.Count} final · {crafts} {(crafts == 1 ? "sub-craft" : "sub-crafts")} · {gather} gather · {vendor} vendor/scrip";
    }

    private void DrawTreeNode(TreeNode node, int depth, bool isLast)
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        var indent = IndentPerLevel;
        var drawList = ImGui.GetWindowDrawList();
        var connector = M3.U32(s.OutlineVariant, 0.9f);
        var thickness = MathF.Max(1f, 1.25f * scale);
        var hasChildren = node.Children.Count > 0;

        var rowTop = ImGui.GetCursorScreenPos().Y;
        var rowBottom = rowTop + _rowHeight;
        var rowCenter = rowTop + _rowHeight * 0.5f;
        var x0 = _originX + depth * indent;
        var slotCenterX = x0 + indent * 0.5f;

        for (var d = 0; d < depth; d++)
        {
            if (d < _ancestorHasMoreSiblings.Count && _ancestorHasMoreSiblings[d])
            {
                var x = _originX + d * indent + indent * 0.5f;
                drawList.AddLine(new Vector2(x, rowTop), new Vector2(x, rowBottom), connector, thickness);
            }
        }

        if (depth > 0)
        {
            var parentX = slotCenterX - indent;
            var elbowEnd = slotCenterX - (hasChildren ? 6f : 4f) * scale;
            drawList.AddLine(new Vector2(parentX, rowTop), new Vector2(parentX, rowCenter), connector, thickness);
            drawList.AddLine(new Vector2(parentX, rowCenter), new Vector2(elbowEnd, rowCenter), connector, thickness);
            if (!isLast)
            {
                drawList.AddLine(new Vector2(parentX, rowCenter), new Vector2(parentX, rowBottom), connector, thickness);
            }
        }

        if (hasChildren && node.Expanded)
        {
            drawList.AddLine(new Vector2(slotCenterX, rowCenter + 6f * scale), new Vector2(slotCenterX, rowBottom), connector, thickness);
        }

        var labelWidth = LabelWidth(node);
        var rowWidth = MathF.Max(_visibleWidth - depth * indent, LabelOffset + labelWidth + 8f * scale);
        var (tag, tone) = NodeTag(node);
        var accessoryIcons = CraftingRowIcons.GetMaterialIcons(node.ItemId, node.IsCraft);

        using (ImRaii.PushId(node.NodeId))
        {
            ImGui.SetCursorScreenPos(new Vector2(x0, rowTop));
            ImGui.BeginGroup();

            var clicked = ImGui.InvisibleButton("##row", new Vector2(rowWidth, _rowHeight));
            ImGui.SetItemAllowOverlap();
            var hovered = ImGui.IsItemHovered();
            var held = ImGui.IsItemActive();
            if (clicked && hasChildren)
            {
                node.Expanded = !node.Expanded;
            }

            if (hovered || held)
            {
                drawList.AddRectFilled(new Vector2(x0, rowTop), new Vector2(x0 + rowWidth, rowBottom),
                    M3.U32(s.OnSurface, held && hasChildren ? M3.StatePressed : M3.StateHover), M3.ShapeSmall);
                if (hasChildren)
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                }
            }

            if (hasChildren)
            {
                M3Draw.Chevron(drawList, new Vector2(slotCenterX, rowCenter), node.Expanded ? 1f : 0f,
                    M3.Alpha(s.OnSurfaceVariant, hovered ? 1f : 0.85f), indent * 0.8f * 0.22f);
            }
            else
            {
                drawList.AddCircleFilled(new Vector2(slotCenterX, rowCenter), MathF.Max(1.5f, 2.5f * scale), connector, 12);
            }

            var x = x0 + indent;
            var icon = M3ActionIcon.GameIcon(node.IconId);
            if (icon != null)
            {
                M3Draw.Texture(drawList, icon, new Vector2(x, rowTop + (_rowHeight - _iconSize) * 0.5f), _iconSize);
            }

            x += _iconSize + _gap;
            M3Badge.Paint(drawList, new Vector2(x, rowTop + (_rowHeight - _tagSize.Y) * 0.5f), tag, tone);
            x += _tagSize.X + _gap;

            var textY = rowTop + (_rowHeight - ImGui.GetTextLineHeight()) * 0.5f;
            drawList.AddText(new Vector2(x, textY), M3.U32(s.OnSurface), node.ItemName);
            if (node.QuantityText.Length > 0)
            {
                drawList.AddText(new Vector2(x + ImGui.CalcTextSize(node.ItemName).X, textY), M3.U32(s.OnSurfaceVariant), node.QuantityText);
            }

            var labelEnd = x + labelWidth;
            if (accessoryIcons.Count > 0)
            {
                var accessorySize = MathF.Min(_iconSize, VulcanUiScaling.Scaled(16f));
                ImGui.SetCursorScreenPos(new Vector2(MathF.Max(_iconColumnScreenX, labelEnd + IconOverflowGap),
                    rowTop + (_rowHeight - accessorySize) * 0.5f));
                CraftingRowIcons.DrawIconsRightAligned(accessoryIcons, accessorySize);
            }

            ImGui.EndGroup();

            var mouseX = ImGui.GetMousePos().X;
            if (hovered && mouseX >= x0 + indent && mouseX <= labelEnd + _gap)
            {
                using var tooltipSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, _itemSpacing);
                DrawNodeTooltip(node, tone);
            }
        }

        if (!node.Expanded || !hasChildren)
        {
            return;
        }

        if (_ancestorHasMoreSiblings.Count <= depth)
        {
            _ancestorHasMoreSiblings.Add(false);
        }

        for (var i = 0; i < node.Children.Count; i++)
        {
            _ancestorHasMoreSiblings[depth] = i < node.Children.Count - 1;
            DrawTreeNode(node.Children[i], depth + 1, i == node.Children.Count - 1);
        }
        if (_ancestorHasMoreSiblings.Count > depth)
        {
            _ancestorHasMoreSiblings.RemoveAt(_ancestorHasMoreSiblings.Count - 1);
        }
    }

    private static Vector4 Tone(Vector4 hue)
        => M3.CustomColor(hue).Color;

    private static (string Label, Vector4 Tone) NodeTag(TreeNode node)
    {
        if (node.IsRoot)
        {
            return ("Final", Tone(HueRoot));
        }

        if (node.IsCraft)
        {
            return ("Craft", Tone(HueSubCraft));
        }

        return node.MaterialSource switch
        {
            MaterialSource.Gatherable => ("Gather", Tone(HueGather)),
            MaterialSource.Fish => ("Fish", Tone(HueFish)),
            MaterialSource.GilVendor => ("Vendor", Tone(HueVendor)),
            MaterialSource.SpecialCurrency => ("Currency", Tone(HueScrip)),
            MaterialSource.Scrip => ("Scrip", Tone(HueScrip)),
            MaterialSource.Drop => ("Drop", Tone(HueDrop)),
            _ => ("Other", M3.Scheme.OnSurfaceVariant),
        };
    }

    private static void DrawNodeTooltip(TreeNode node, Vector4 tone)
    {
        ImGui.BeginTooltip();
        using (ImRaii.PushFont(M3.TitleMedium))
        {
            ImGui.TextUnformatted(node.ItemName);
        }

        M3Text.Draw($"Need: {node.Quantity:N0}", M3TextStyle.Muted);
        if (node.IsCraft && node.RecipeYield > 1 && node.CraftCount > 0)
        {
            M3Text.Draw($"Crafts: {node.CraftCount:N0} (yields {node.RecipeYield} each)", M3TextStyle.Muted);
        }

        if (node.IsCraft && node.CrafterClassName is { Length: > 0 })
        {
            TextColored($"Crafter: {node.CrafterClassName}", tone);
        }

        if (node.GatherTerritory is { Length: > 0 })
        {
            TextColored($"Best location: {node.GatherTerritory}", tone);
        }

        if (node.VendorOptions is { Count: > 0 })
        {
            ImGui.Dummy(new Vector2(0f, 2f * M3.Scale));
            TextColored("Vendors", tone);
            var shown = Math.Min(node.VendorOptions.Count, MaxVendorsShown);
            for (var i = 0; i < shown; ++i)
            {
                var option = node.VendorOptions[i];
                M3Text.Bullet($"{option.NpcName}: {option.Cost:N0} {option.CurrencyName}", wrap: false, dot: tone);
            }

            if (node.VendorOptions.Count > MaxVendorsShown)
            {
                M3Text.Draw($"and {node.VendorOptions.Count - MaxVendorsShown} more", M3TextStyle.Faint);
            }
        }

        ImGui.EndTooltip();
    }

    private static void TextColored(string text, Vector4 color)
    {
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
    }

    private bool ShouldRebuild()
    {
        if (_editor == null)
        {
            return false;
        }

        if (!ReferenceEquals(_cachedEditor, _editor))
        {
            return true;
        }

        if (_cachedVersion != _editor.MaterialCacheVersion)
        {
            return true;
        }

        return _cachedMobDropInfoInitialized != MobDropInfoCache.IsInitialized;
    }

    private void InvalidateTree()
    {
        _cachedTree = [];
        _cachedVersion = -1;
        _cachedEditor = null;
        _cachedMobDropInfoInitialized = false;
        _cachedSummary = string.Empty;
    }

    private void RebuildTree()
    {
        _cachedTree = [];
        if (_editor == null)
        {
            return;
        }

        try
        {
            var list = _editor.PlanningList;
            var plan = list.CreatePlan();

            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            if (itemSheet == null)
            {
                return;
            }

            var nextId = 0;
            foreach (var original in plan.OriginalRecipes)
            {
                if (original.Quantity <= 0)
                {
                    continue;
                }

                var recipe = RecipeManager.GetRecipe(original.RecipeId);
                if (!recipe.HasValue)
                {
                    continue;
                }

                var resultItemId = recipe.Value.ItemResult.RowId;
                if (!itemSheet.TryGetRow(resultItemId, out var resultItem))
                {
                    continue;
                }

                var craftCount = original.Quantity;
                var totalItems = craftCount * (int)recipe.Value.AmountResult;

                var rootNode = new TreeNode
                {
                    NodeId = nextId++,
                    ItemId = resultItemId,
                    IconId = resultItem.Icon,
                    ItemName = resultItem.Name.ExtractText(),
                    Quantity = totalItems,
                    QuantityText = FormatQuantity(totalItems),
                    CraftCount = craftCount,
                    RecipeYield = (int)recipe.Value.AmountResult,
                    IsCraft = true,
                    IsRoot = true,
                    Expanded = true,
                    CrafterClassName = ResolveCrafterName(recipe.Value),
                };
                BuildChildren(rootNode, recipe.Value, craftCount, list, itemSheet, ref nextId, depth: 0);
                _cachedTree.Add(rootNode);
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingTreeWindow] Failed to rebuild tree for list '{_editor?.ListName}': {ex.Message}");
            _cachedTree = [];
        }

        _cachedEditor = _editor;
        _cachedVersion = _editor?.MaterialCacheVersion ?? -1;
        _cachedMobDropInfoInitialized = MobDropInfoCache.IsInitialized;
        _cachedSummary = _cachedTree.Count > 0 ? BuildSummary(_cachedTree) : string.Empty;
    }

    private static string FormatQuantity(int quantity)
        => quantity > 0 ? $" × {quantity}" : string.Empty;

    private static void BuildChildren(
        TreeNode parent,
        Recipe recipe,
        int craftCount,
        CraftingListDefinition list,
        ExcelSheet<Item> itemSheet,
        ref int nextId,
        int depth)
    {
        if (depth >= MaxRecursionDepth)
        {
            return;
        }

        foreach (var (ingredientItemId, ingredientPerCraft) in RecipeManager.GetIngredients(recipe))
        {
            if (!itemSheet.TryGetRow(ingredientItemId, out var ingItem))
            {
                continue;
            }

            var totalNeeded = ingredientPerCraft * craftCount;
            var subRecipe = ResolveSubRecipe(list, ingredientItemId);
            if (subRecipe.HasValue)
            {
                var subRecipeYield = (int)subRecipe.Value.AmountResult;
                var subCraftCount = subRecipeYield > 0
                    ? (int)Math.Ceiling((double)totalNeeded / subRecipeYield)
                    : totalNeeded;
                var child = new TreeNode
                {
                    NodeId = nextId++,
                    ItemId = ingredientItemId,
                    IconId = ingItem.Icon,
                    ItemName = ingItem.Name.ExtractText(),
                    Quantity = totalNeeded,
                    QuantityText = FormatQuantity(totalNeeded),
                    CraftCount = subCraftCount,
                    RecipeYield = subRecipeYield,
                    IsCraft = true,
                    Expanded = depth < 1,
                    CrafterClassName = ResolveCrafterName(subRecipe.Value),
                };
                BuildChildren(child, subRecipe.Value, subCraftCount, list, itemSheet, ref nextId, depth + 1);
                parent.Children.Add(child);
            }
            else
            {
                var classification = MaterialSourceClassifier.Classify(ingredientItemId);
                var leaf = new TreeNode
                {
                    NodeId = nextId++,
                    ItemId = ingredientItemId,
                    IconId = ingItem.Icon,
                    ItemName = ingItem.Name.ExtractText(),
                    Quantity = totalNeeded,
                    QuantityText = FormatQuantity(totalNeeded),
                    IsCraft = false,
                    Expanded = false,
                    MaterialSource = classification,
                    GatherTerritory = ResolveGatherTerritory(ingredientItemId, classification),
                    VendorOptions = ResolveVendorOptions(ingredientItemId, classification),
                };
                parent.Children.Add(leaf);
            }
        }
    }

    private static Recipe? ResolveSubRecipe(CraftingListDefinition list, uint itemId)
    {
        if (list.PrecraftRecipeOverrides.TryGetValue(itemId, out var overrideId))
        {
            var overrideRecipe = RecipeManager.GetRecipe(overrideId);
            if (overrideRecipe.HasValue)
            {
                return overrideRecipe;
            }
        }
        return RecipeManager.GetRecipeForItem(itemId);
    }

    private static string ResolveCrafterName(Recipe recipe)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<ClassJob>();
        if (sheet == null)
        {
            return string.Empty;
        }

        var jobId = recipe.CraftType.RowId + 8;
        return sheet.TryGetRow(jobId, out var job) ? job.Name.ExtractText() : string.Empty;
    }

    private static string ResolveGatherTerritory(uint itemId, MaterialSource source)
    {
        if (source != MaterialSource.Gatherable && source != MaterialSource.Fish)
        {
            return string.Empty;
        }

        try
        {
            if (GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var gatherable))
            {
                if (gatherable.NodeList.Count > 0)
                {
                    return gatherable.NodeList[0].Territory.Name;
                }
            }
            if (GatherBuddy.GameData.Fishes.TryGetValue(itemId, out var fish))
            {
                if (fish.FishingSpots.Count > 0)
                {
                    return fish.FishingSpots[0].Territory.Name;
                }
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Verbose($"[CraftingTreeWindow] Could not resolve gather territory for {itemId}: {ex.Message}");
        }
        return string.Empty;
    }

    private static List<VendorOption> ResolveVendorOptions(uint itemId, MaterialSource source)
    {
        if (source != MaterialSource.GilVendor
         && source != MaterialSource.SpecialCurrency
         && source != MaterialSource.Scrip)
        {
            return [];
        }

        var options = new List<VendorOption>();
        try
        {
            foreach (var entry in VendorShopResolver.GilShopEntries)
            {
                if (entry.ItemId != itemId)
                {
                    continue;
                }

                AddVendorEntry(options, entry);
            }
            foreach (var entry in VendorShopResolver.GcShopEntries)
            {
                if (entry.ItemId != itemId)
                {
                    continue;
                }

                AddVendorEntry(options, entry);
            }
            foreach (var entry in VendorShopResolver.SpecialShopEntries)
            {
                if (entry.ItemId != itemId)
                {
                    continue;
                }

                AddVendorEntry(options, entry);
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Verbose($"[CraftingTreeWindow] Could not resolve vendor options for {itemId}: {ex.Message}");
        }
        return options;
    }

    private static void AddVendorEntry(List<VendorOption> options, VendorShopEntry entry)
    {
        if (entry.Npcs == null || entry.Npcs.Count == 0)
        {
            options.Add(new VendorOption("(Unknown vendor)", entry.Cost, entry.CurrencyName));
            return;
        }
        foreach (var npc in entry.Npcs)
        {
            options.Add(new VendorOption(npc.Name, entry.Cost, entry.CurrencyName));
        }
    }

    private sealed class TreeNode
    {
        public int NodeId;
        public uint ItemId;
        public ushort IconId;
        public string ItemName = string.Empty;
        public string QuantityText = string.Empty;
        public int Quantity;
        public int CraftCount;
        public int RecipeYield;
        public bool IsCraft;
        public bool IsRoot;
        public bool Expanded;
        public MaterialSource? MaterialSource;
        public string? CrafterClassName;
        public string? GatherTerritory;
        public List<VendorOption>? VendorOptions;
        public List<TreeNode> Children { get; } = [];
    }

    private readonly record struct VendorOption(string NpcName, uint Cost, string CurrencyName);
}
