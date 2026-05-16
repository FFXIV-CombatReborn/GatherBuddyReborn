using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using ElliLib;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using GatherBuddy.Vulcan.Vendors;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Gui;

public class CraftingTreeWindow : Window
{
    private CraftingListEditor? _editor;

    private static readonly Vector4 ColorRoot     = new(0.95f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 ColorSubCraft = new(0.45f, 0.90f, 0.95f, 1f);
    private static readonly Vector4 ColorGather   = new(0.55f, 1.00f, 0.55f, 1f);
    private static readonly Vector4 ColorFish     = new(0.55f, 0.85f, 1.00f, 1f);
    private static readonly Vector4 ColorVendor   = new(1.00f, 0.85f, 0.30f, 1f);
    private static readonly Vector4 ColorScrip    = new(0.85f, 0.55f, 1.00f, 1f);
    private static readonly Vector4 ColorDrop     = new(1.00f, 0.55f, 0.55f, 1f);
    private static readonly Vector4 ColorOther    = new(0.70f, 0.70f, 0.70f, 1f);
    private static readonly Vector4 ColorMuted    = new(0.55f, 0.55f, 0.60f, 1f);
    private static readonly Vector4 ColorConnector = new(0.55f, 0.55f, 0.62f, 0.9f);

    private const float IndentPerLevel = 22f;
    private const float ConnectorThickness = 1.4f;
    private const int MaxRecursionDepth = 24;
    private const float IconColumnPadding = 12f;

    private List<TreeNode> _cachedTree = [];
    private long _cachedVersion = -1;
    private CraftingListEditor? _cachedEditor;

    private readonly List<bool> _ancestorHasMoreSiblings = new();
    private float _iconColumnScreenX;

    public CraftingTreeWindow() : base("Crafting Tree###CraftingTree")
    {
        Size           = new Vector2(560, 600);
        SizeCondition  = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(380, 240),
            MaximumSize = new Vector2(1400, 1600),
        };
    }

    public void SetEditor(CraftingListEditor? editor)
    {
        if (!ReferenceEquals(_editor, editor))
            InvalidateTree();
        _editor = editor;
    }

    public override void PreDraw()
    {
        if (_editor != null)
            WindowName = $"Crafting Tree — {_editor.ListName}###CraftingTree";
    }

    public override void Draw()
    {
        if (_editor == null)
        {
            ImGui.TextColored(ColorMuted, "No list open.");
            return;
        }

        using var theme = VulcanUiStyle.PushTheme();

        if (ShouldRebuild())
            RebuildTree();

        if (_cachedTree.Count == 0)
        {
            ImGui.TextColored(ColorMuted, "List is empty or has no resolvable recipes.");
            return;
        }

        DrawHeader();
        ImGui.Separator();

        ImGui.BeginChild("##craftingTreeBody", new Vector2(0, 0), false, ImGuiWindowFlags.HorizontalScrollbar);
        var bodyOriginX = ImGui.GetCursorScreenPos().X;
        _iconColumnScreenX = bodyOriginX + ComputeMaxLabelEndOffset() + IconColumnPadding;
        _ancestorHasMoreSiblings.Clear();
        for (var i = 0; i < _cachedTree.Count; i++)
            DrawTreeNode(_cachedTree[i], depth: 0, isLast: i == _cachedTree.Count - 1);
        ImGui.EndChild();
    }

    private float ComputeMaxLabelEndOffset()
    {
        var lineHeight    = ImGui.GetTextLineHeight();
        var disclosureW   = lineHeight;
        var iconW         = lineHeight;
        const float postDisclosure = 2f;
        const float postIcon       = 4f;

        var max = 0f;
        foreach (var root in _cachedTree)
            WalkForMaxLabelEnd(root, depth: 0, disclosureW, iconW, postDisclosure, postIcon, ref max);
        return max;
    }

    private static void WalkForMaxLabelEnd(TreeNode node, int depth, float disclosureW, float iconW,
        float postDisclosure, float postIcon, ref float max)
    {
        var (label, _) = BuildNodeLabel(node);
        var labelW = ImGui.CalcTextSize(label).X;
        var rowEnd = depth * IndentPerLevel + disclosureW + postDisclosure + iconW + postIcon + labelW;
        if (rowEnd > max)
            max = rowEnd;

        if (!node.Expanded)
            return;
        foreach (var child in node.Children)
            WalkForMaxLabelEnd(child, depth + 1, disclosureW, iconW, postDisclosure, postIcon, ref max);
    }

    private void DrawHeader()
    {
        var roots = _cachedTree.Count;
        var totalCrafts = SumCrafts(_cachedTree);
        var totalGather = SumLeaves(_cachedTree, MaterialSource.Gatherable, MaterialSource.Fish);
        var totalVendor = SumLeaves(_cachedTree, MaterialSource.GilVendor, MaterialSource.SpecialCurrency, MaterialSource.Scrip);

        ImGui.TextColored(ColorMuted, $"{roots} root(s)  ·  {totalCrafts} sub-craft(s)  ·  {totalGather} gather  ·  {totalVendor} vendor/scrip");
        ImGui.SameLine();
        if (ImGui.SmallButton("Expand All##craftingTreeExpand"))
            SetExpandedAll(true);
        ImGui.SameLine();
        if (ImGui.SmallButton("Collapse All##craftingTreeCollapse"))
            SetExpandedAll(false);
    }

    private void SetExpandedAll(bool expanded)
    {
        foreach (var root in _cachedTree)
            SetExpandedRecursive(root, expanded);
    }

    private static void SetExpandedRecursive(TreeNode node, bool expanded)
    {
        node.Expanded = expanded;
        foreach (var child in node.Children)
            SetExpandedRecursive(child, expanded);
    }

    private static int SumCrafts(IEnumerable<TreeNode> nodes)
    {
        var total = 0;
        foreach (var node in nodes)
        {
            if (node.IsCraft && !node.IsRoot)
                total++;
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
                total++;
            total += SumLeaves(node.Children, sources);
        }
        return total;
    }

    private void DrawTreeNode(TreeNode node, int depth, bool isLast)
    {
        var drawList   = ImGui.GetWindowDrawList();
        var lineHeight = ImGui.GetTextLineHeight();
        var rowHeight  = ImGui.GetTextLineHeightWithSpacing();
        var connectorColor = ImGui.ColorConvertFloat4ToU32(ColorConnector);

        var rowOriginScreen = ImGui.GetCursorScreenPos();
        var rowYTop    = rowOriginScreen.Y;
        var rowYCenter = rowYTop + lineHeight * 0.5f;
        var rowYBottom = rowYTop + rowHeight;

        for (var d = 0; d < depth; d++)
        {
            if (d < _ancestorHasMoreSiblings.Count && _ancestorHasMoreSiblings[d])
            {
                var x = rowOriginScreen.X + d * IndentPerLevel + IndentPerLevel * 0.5f;
                drawList.AddLine(new Vector2(x, rowYTop), new Vector2(x, rowYBottom), connectorColor, ConnectorThickness);
            }
        }

        if (depth > 0)
        {
            var parentX  = rowOriginScreen.X + (depth - 1) * IndentPerLevel + IndentPerLevel * 0.5f;
            var myStartX = rowOriginScreen.X + depth * IndentPerLevel;

            drawList.AddLine(new Vector2(parentX, rowYTop), new Vector2(parentX, rowYCenter), connectorColor, ConnectorThickness);
            drawList.AddLine(new Vector2(parentX, rowYCenter), new Vector2(myStartX, rowYCenter), connectorColor, ConnectorThickness);
            if (!isLast)
                drawList.AddLine(new Vector2(parentX, rowYCenter), new Vector2(parentX, rowYBottom), connectorColor, ConnectorThickness);
        }

        if (depth > 0)
        {
            ImGui.Dummy(new Vector2(depth * IndentPerLevel, 0));
            ImGui.SameLine(0, 0);
        }

        var hasChildren = node.Children.Count > 0;
        if (hasChildren)
        {
            var arrowSize = lineHeight;
            var arrowScreenPos = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton($"##arrow_{node.NodeId}", new Vector2(arrowSize, arrowSize)))
                node.Expanded = !node.Expanded;
            DrawDisclosureTriangle(drawList, arrowScreenPos, arrowSize, node.Expanded);
        }
        else
        {
            DrawLeafBullet(drawList, ImGui.GetCursorScreenPos(), lineHeight);
            ImGui.Dummy(new Vector2(lineHeight, lineHeight));
        }
        ImGui.SameLine(0, 2);

        DrawIcon(node.IconId, lineHeight);
        ImGui.SameLine(0, 4);

        var labelStart = ImGui.GetCursorScreenPos();
        var (label, color) = BuildNodeLabel(node);
        ImGui.TextColored(color, label);

        var accessoryIcons = CraftingRowIcons.GetMaterialIcons(node.ItemId, node.IsCraft);
        if (accessoryIcons.Count > 0)
        {
            ImGui.SameLine(0, 0);
            var currentX = ImGui.GetCursorScreenPos().X;
            var gap      = _iconColumnScreenX - currentX;
            if (gap > 0)
            {
                ImGui.Dummy(new Vector2(gap, 0));
                ImGui.SameLine(0, 0);
            }
            else
            {
                ImGui.Dummy(new Vector2(6f, 0));
                ImGui.SameLine(0, 0);
            }
            CraftingRowIcons.DrawIconsRightAligned(accessoryIcons);
        }

        var labelMax = ImGui.GetItemRectMax();
        var hoverMin = new Vector2(labelStart.X - 2, rowYTop);
        var hoverMax = new Vector2(Math.Max(labelMax.X + 4, labelStart.X + 8), rowYTop + rowHeight);
        if (ImGui.IsMouseHoveringRect(hoverMin, hoverMax) && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
            DrawNodeTooltip(node);

        if (!node.Expanded || !hasChildren)
            return;

        if (_ancestorHasMoreSiblings.Count <= depth)
            _ancestorHasMoreSiblings.Add(false);
        for (var i = 0; i < node.Children.Count; i++)
        {
            _ancestorHasMoreSiblings[depth] = i < node.Children.Count - 1;
            DrawTreeNode(node.Children[i], depth + 1, i == node.Children.Count - 1);
        }
        if (_ancestorHasMoreSiblings.Count > depth)
            _ancestorHasMoreSiblings.RemoveAt(_ancestorHasMoreSiblings.Count - 1);
    }

    private static void DrawDisclosureTriangle(ImDrawListPtr drawList, Vector2 origin, float size, bool expanded)
    {
        var color = ImGui.ColorConvertFloat4ToU32(new Vector4(0.85f, 0.85f, 0.88f, 0.95f));
        var center = new Vector2(origin.X + size * 0.5f, origin.Y + size * 0.5f);
        var arm    = size * 0.28f;
        if (expanded)
        {
            var p1 = new Vector2(center.X - arm,        center.Y - arm * 0.55f);
            var p2 = new Vector2(center.X + arm,        center.Y - arm * 0.55f);
            var p3 = new Vector2(center.X,              center.Y + arm * 0.85f);
            drawList.AddTriangleFilled(p1, p2, p3, color);
        }
        else
        {
            var p1 = new Vector2(center.X - arm * 0.55f, center.Y - arm);
            var p2 = new Vector2(center.X + arm * 0.85f, center.Y);
            var p3 = new Vector2(center.X - arm * 0.55f, center.Y + arm);
            drawList.AddTriangleFilled(p1, p2, p3, color);
        }
    }

    private static void DrawLeafBullet(ImDrawListPtr drawList, Vector2 origin, float size)
    {
        var color = ImGui.ColorConvertFloat4ToU32(ColorConnector);
        var center = new Vector2(origin.X + size * 0.5f, origin.Y + size * 0.5f);
        drawList.AddCircleFilled(center, Math.Max(2f, size * 0.12f), color, 8);
    }

    private static (string Text, Vector4 Color) BuildNodeLabel(TreeNode node)
    {
        var qty = node.Quantity > 0 ? $" × {node.Quantity}" : string.Empty;
        if (node.IsRoot)
            return ($"[Final] {node.ItemName}{qty}", ColorRoot);
        if (node.IsCraft)
            return ($"[Sub-craft] {node.ItemName}{qty}", ColorSubCraft);

        var color = node.MaterialSource switch
        {
            MaterialSource.Gatherable      => ColorGather,
            MaterialSource.Fish            => ColorFish,
            MaterialSource.GilVendor       => ColorVendor,
            MaterialSource.SpecialCurrency => ColorScrip,
            MaterialSource.Scrip           => ColorScrip,
            MaterialSource.Drop            => ColorDrop,
            _                               => ColorOther,
        };
        var prefix = node.MaterialSource switch
        {
            MaterialSource.Gatherable      => "[Gather]",
            MaterialSource.Fish            => "[Fish]",
            MaterialSource.GilVendor       => "[Vendor]",
            MaterialSource.SpecialCurrency => "[Currency]",
            MaterialSource.Scrip           => "[Scrip]",
            MaterialSource.Drop            => "[Drop]",
            _                               => "[Other]",
        };
        return ($"{prefix} {node.ItemName}{qty}", color);
    }

    private static void DrawNodeTooltip(TreeNode node)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(node.ItemName);
        ImGui.Separator();
        ImGui.TextColored(ColorMuted, $"Need: {node.Quantity}");

        if (node.IsCraft && node.RecipeYield > 1 && node.CraftCount > 0)
            ImGui.TextColored(ColorMuted, $"Crafts: {node.CraftCount} (yields {node.RecipeYield} ea.)");

        if (node.GatherTerritory is { Length: > 0 })
            ImGui.TextColored(ColorGather, $"Best location: {node.GatherTerritory}");

        if (node.VendorOptions is { Count: > 0 })
        {
            ImGui.TextColored(ColorVendor, "Vendors:");
            foreach (var opt in node.VendorOptions.Take(6))
                ImGui.BulletText($"{opt.NpcName} — {opt.Cost} {opt.CurrencyName}");
            if (node.VendorOptions.Count > 6)
                ImGui.TextColored(ColorMuted, $"  + {node.VendorOptions.Count - 6} more");
        }

        if (node.IsCraft && node.CrafterClassName is { Length: > 0 })
            ImGui.TextColored(ColorSubCraft, $"Crafter: {node.CrafterClassName}");

        ImGui.EndTooltip();
    }

    private static void DrawIcon(uint iconId, float size)
    {
        if (iconId == 0)
        {
            ImGui.Dummy(new Vector2(size, size));
            return;
        }
        var icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId));
        if (icon.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, new Vector2(size, size));
        else
            ImGui.Dummy(new Vector2(size, size));
    }

    private bool ShouldRebuild()
    {
        if (_editor == null)
            return false;
        if (!ReferenceEquals(_cachedEditor, _editor))
            return true;
        return _cachedVersion != _editor.MaterialCacheVersion;
    }

    private void InvalidateTree()
    {
        _cachedTree = [];
        _cachedVersion = -1;
        _cachedEditor = null;
    }

    private void RebuildTree()
    {
        _cachedTree = [];
        if (_editor == null)
            return;

        try
        {
            var list = _editor.PlanningList;
            var plan = list.CreatePlan();

            var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
            if (itemSheet == null)
                return;

            var nextId = 0;
            foreach (var original in plan.OriginalRecipes)
            {
                if (original.Quantity <= 0)
                    continue;
                var recipe = RecipeManager.GetRecipe(original.RecipeId);
                if (!recipe.HasValue)
                    continue;

                var resultItemId = recipe.Value.ItemResult.RowId;
                if (!itemSheet.TryGetRow(resultItemId, out var resultItem))
                    continue;

                var craftCount = original.Quantity;
                var totalItems = craftCount * (int)recipe.Value.AmountResult;

                var rootNode = new TreeNode
                {
                    NodeId       = nextId++,
                    ItemId       = resultItemId,
                    IconId       = resultItem.Icon,
                    ItemName     = resultItem.Name.ExtractText(),
                    Quantity     = totalItems,
                    CraftCount   = craftCount,
                    RecipeYield  = (int)recipe.Value.AmountResult,
                    IsCraft      = true,
                    IsRoot       = true,
                    Expanded     = true,
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

        _cachedEditor  = _editor;
        _cachedVersion = _editor?.MaterialCacheVersion ?? -1;
    }

    private static void BuildChildren(
        TreeNode parent,
        Recipe   recipe,
        int      craftCount,
        CraftingListDefinition list,
        ExcelSheet<Item> itemSheet,
        ref int  nextId,
        int      depth)
    {
        if (depth >= MaxRecursionDepth)
            return;

        foreach (var (ingredientItemId, ingredientPerCraft) in RecipeManager.GetIngredients(recipe))
        {
            if (!itemSheet.TryGetRow(ingredientItemId, out var ingItem))
                continue;

            var totalNeeded = ingredientPerCraft * craftCount;
            var subRecipe = ResolveSubRecipe(list, ingredientItemId);
            if (subRecipe.HasValue)
            {
                var subRecipeYield = (int)subRecipe.Value.AmountResult;
                var subCraftCount = subRecipeYield > 0
                    ? (int)Math.Ceiling((double)totalNeeded / subRecipeYield)
                    : totalNeeded;
                var producedItems = subCraftCount * Math.Max(subRecipeYield, 1);
                var child = new TreeNode
                {
                    NodeId       = nextId++,
                    ItemId       = ingredientItemId,
                    IconId       = ingItem.Icon,
                    ItemName     = ingItem.Name.ExtractText(),
                    Quantity     = producedItems,
                    CraftCount   = subCraftCount,
                    RecipeYield  = subRecipeYield,
                    IsCraft      = true,
                    Expanded     = depth < 1,
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
                    NodeId         = nextId++,
                    ItemId         = ingredientItemId,
                    IconId         = ingItem.Icon,
                    ItemName       = ingItem.Name.ExtractText(),
                    Quantity       = totalNeeded,
                    IsCraft        = false,
                    Expanded       = false,
                    MaterialSource = classification,
                    GatherTerritory = ResolveGatherTerritory(ingredientItemId, classification),
                    VendorOptions   = ResolveVendorOptions(ingredientItemId, classification),
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
                return overrideRecipe;
        }
        return RecipeManager.GetRecipeForItem(itemId);
    }

    private static string ResolveCrafterName(Recipe recipe)
    {
        var sheet = Dalamud.GameData.GetExcelSheet<ClassJob>();
        if (sheet == null)
            return string.Empty;
        var jobId = recipe.CraftType.RowId + 8;
        return sheet.TryGetRow(jobId, out var job) ? job.Name.ExtractText() : string.Empty;
    }

    private static string ResolveGatherTerritory(uint itemId, MaterialSource source)
    {
        if (source != MaterialSource.Gatherable && source != MaterialSource.Fish)
            return string.Empty;
        try
        {
            if (GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var gatherable))
            {
                var node = gatherable.NodeList.FirstOrDefault();
                if (node != null)
                    return node.Territory.Name;
            }
            if (GatherBuddy.GameData.Fishes.TryGetValue(itemId, out var fish))
            {
                var spot = fish.FishingSpots.FirstOrDefault();
                if (spot != null)
                    return spot.Territory.Name;
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
            return [];

        var options = new List<VendorOption>();
        try
        {
            foreach (var entry in VendorShopResolver.GilShopEntries)
            {
                if (entry.ItemId != itemId)
                    continue;
                AddVendorEntry(options, entry);
            }
            foreach (var entry in VendorShopResolver.GcShopEntries)
            {
                if (entry.ItemId != itemId)
                    continue;
                AddVendorEntry(options, entry);
            }
            foreach (var entry in VendorShopResolver.SpecialShopEntries)
            {
                if (entry.ItemId != itemId)
                    continue;
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
            options.Add(new VendorOption(npc.Name, entry.Cost, entry.CurrencyName));
    }

    private sealed class TreeNode
    {
        public int NodeId;
        public uint ItemId;
        public ushort IconId;
        public string ItemName = string.Empty;
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
        public List<TreeNode> Children { get; } = new();
    }

    private readonly record struct VendorOption(string NpcName, uint Cost, string CurrencyName);
}
