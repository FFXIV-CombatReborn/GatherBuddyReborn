using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using ElliLib.Raii;
using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private string _solutionsSearch = string.Empty;
    private string? _selectedSolutionKey = null;
    private List<CachedRaphaelSolution> _solutionsList = new();
    private DateTime _solutionsLastRefresh = DateTime.MinValue;
    private readonly Dictionary<uint, (string Name, uint IconId)> _solutionItemCache = new();

    private void DrawSolutionsTab()
    {
        IDisposable tabItem;
        bool tabOpen;

        if (GatherBuddy.ControllerSupport != null)
        {
            var handle = GatherBuddy.ControllerSupport.TabNavigation.TabItem("Solutions##solutionsTab", 5, 9);
            tabItem = handle;
            tabOpen = handle;
        }
        else
        {
            var handle = ImRaii.TabItem("Solutions##solutionsTab");
            tabItem = handle;
            tabOpen = handle.Success;
        }

        using (tabItem)
        {
            if (!tabOpen)
                return;

            if ((DateTime.UtcNow - _solutionsLastRefresh).TotalSeconds > 0.5)
            {
                _solutionsList = GatherBuddy.RaphaelSolveCoordinator
                    .GetAllCachedSolutions()
                    .OrderByDescending(s => s.GeneratedAt)
                    .ToList();
                _solutionsLastRefresh = DateTime.UtcNow;
            }

            var coordinator = GatherBuddy.RaphaelSolveCoordinator;
            var raphaelConfig = GatherBuddy.Config.RaphaelSolverConfig;

            ImGui.SetNextItemWidth(220);
            ImGui.InputTextWithHint("##solutionsSearch", "Search items...", ref _solutionsSearch, 128);
            ImGui.SameLine();
            if (ImGui.Button("Clear All", new Vector2(90, 0)))
            {
                coordinator.Clear();
                _selectedSolutionKey = null;
                _solutionsList.Clear();
            }
            ImGui.SameLine();
            var autoClear = raphaelConfig.AutoClearSolutionCache;
            if (ImGui.Checkbox("Auto-clear on queue start", ref autoClear))
            {
                raphaelConfig.AutoClearSolutionCache = autoClear;
                GatherBuddy.Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Clears cached solutions each time a new crafting queue starts.\nDisable to reuse solutions across queue runs when your stats haven't changed.");

            var activeColor = coordinator.ActiveSolves > 0 ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey;
            var pendingColor = coordinator.PendingSolves > 0 ? ImGuiColors.DalamudOrange : ImGuiColors.DalamudGrey;
            ImGui.SameLine();
            ImGui.TextColored(activeColor, $"Active: {coordinator.ActiveSolves}");
            ImGui.SameLine();
            ImGui.TextColored(pendingColor, $"Pending: {coordinator.PendingSolves}");

            ImGui.Separator();
            ImGui.Spacing();

            var filtered = string.IsNullOrWhiteSpace(_solutionsSearch)
                ? _solutionsList
                : _solutionsList
                    .Where(s => GetSolutionItemName(s.Request.RecipeId)
                        .Contains(_solutionsSearch, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (filtered.Count == 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, _solutionsList.Count == 0
                    ? "No cached solutions. Start a queue with Raphael solver to generate solutions."
                    : "No solutions match your search.");
                return;
            }

            var avail = ImGui.GetContentRegionAvail();
            var leftWidth = 290f;
            var rightWidth = avail.X - leftWidth - ImGui.GetStyle().ItemSpacing.X;

            using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
            {
                ImGui.BeginChild("##solLeftPanel", new Vector2(leftWidth, avail.Y), true);
                DrawSolutionsList(filtered);
                ImGui.EndChild();
            }

            ImGui.SameLine();

            using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.10f, 1.00f)))
            {
                ImGui.BeginChild("##solRightPanel", new Vector2(rightWidth, avail.Y), true);
                DrawSolutionDetail();
                ImGui.EndChild();
            }
        }
    }

    private void DrawSolutionsList(List<CachedRaphaelSolution> solutions)
    {
        var iconSize = new Vector2(28f, 28f);
        var itemHeight = iconSize.Y + ImGui.GetStyle().ItemSpacing.Y;
        var contentMaxX = ImGui.GetContentRegionMax().X;

        ElliLib.ImGuiClip.ClippedDraw(solutions, solution =>
        {
            var (name, iconId) = GetSolutionItemInfo(solution.Request.RecipeId);
            var isSelected = _selectedSolutionKey == solution.Key;
            var req = solution.Request;
            var statsLine = $"Lv{req.Level}  {req.Craftsmanship}/{req.Control}/{req.CP}  {solution.ActionIds.Count} steps";

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
                ImGui.Dummy(iconSize);

            ImGui.SameLine(0, 6);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (iconSize.Y - ImGui.GetTextLineHeight()) / 2f);

            if (ImGui.Selectable($"{name}##sol_{solution.Key}", isSelected, ImGuiSelectableFlags.None,
                    new Vector2(contentMaxX - ImGui.GetCursorPosX(), 0)))
                _selectedSolutionKey = solution.Key;

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(statsLine);
        }, itemHeight);
    }

    private void DrawSolutionDetail()
    {
        if (_selectedSolutionKey == null)
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Select a solution to view details.");
            return;
        }

        var solution = _solutionsList.FirstOrDefault(s => s.Key == _selectedSolutionKey);
        if (solution == null)
        {
            _selectedSolutionKey = null;
            return;
        }

        var req = solution.Request;
        var (name, iconId) = GetSolutionItemInfo(req.RecipeId);
        var largeIconSize = new Vector2(48f, 48f);

        if (iconId > 0)
        {
            var wrap = Icons.DefaultStorage.TextureProvider
                .GetFromGameIcon(new GameIconLookup(iconId))
                .GetWrapOrDefault();
            if (wrap != null)
            {
                ImGui.Image(wrap.Handle, largeIconSize);
                ImGui.SameLine(0, 10);
            }
        }

        var lineH = ImGui.GetTextLineHeightWithSpacing();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (largeIconSize.Y - lineH * 2f) / 2f);
        ImGui.TextColored(ImGuiColors.ParsedGold, name);
        ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Recipe {req.RecipeId}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.DalamudYellow, "Solve Parameters");
        ImGui.Spacing();

        ImGui.Text($"Level:          {req.Level}");
        ImGui.Text($"Craftsmanship:  {req.Craftsmanship}");
        ImGui.SameLine(180);
        ImGui.Text($"Control:  {req.Control}");
        ImGui.SameLine(340);
        ImGui.Text($"CP:  {req.CP}");
        ImGui.Text($"Manipulation:   {(req.Manipulation ? "Yes" : "No")}");
        ImGui.SameLine(180);
        ImGui.Text($"Specialist:  {(req.Specialist ? "Yes" : "No")}");
        ImGui.Text($"Initial Quality: {req.InitialQuality}");
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"Generated: {solution.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.ParsedGold, $"Actions ({solution.ActionIds.Count})");
        ImGui.Spacing();

        var actionIconSize = new Vector2(24f, 24f);
        var remainH = ImGui.GetContentRegionAvail().Y;
        ImGui.BeginChild("##solActions", new Vector2(-1, remainH), false);

        for (var i = 0; i < solution.ActionIds.Count; i++)
        {
            var actionId = solution.ActionIds[i];
            var skillName = ((VulcanSkill)actionId).ToString();
            var skillIconId = GetSkillIconId(actionId);

            if (skillIconId > 0)
            {
                var wrap = Icons.DefaultStorage.TextureProvider
                    .GetFromGameIcon(new GameIconLookup(skillIconId))
                    .GetWrapOrDefault();
                if (wrap != null)
                    ImGui.Image(wrap.Handle, actionIconSize);
                else
                    ImGui.Dummy(actionIconSize);
            }
            else
            {
                ImGui.Dummy(actionIconSize);
            }

            ImGui.SameLine(0, 6);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (actionIconSize.Y - ImGui.GetTextLineHeight()) / 2f);
            ImGui.Text($"{i + 1}. {skillName}");
        }

        ImGui.EndChild();
    }

    private (string Name, uint IconId) GetSolutionItemInfo(uint recipeId)
    {
        if (_solutionItemCache.TryGetValue(recipeId, out var cached))
            return cached;

        try
        {
            var recipe = RecipeManager.GetRecipe(recipeId);
            if (recipe.HasValue)
            {
                var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
                if (itemSheet != null && itemSheet.TryGetRow(recipe.Value.ItemResult.RowId, out var item))
                {
                    var info = (item.Name.ExtractText(), (uint)item.Icon);
                    _solutionItemCache[recipeId] = info;
                    return info;
                }
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[SolutionsTab] Failed to get item info for recipe {recipeId}: {ex.Message}");
        }

        var fallback = ($"Recipe {recipeId}", 0u);
        _solutionItemCache[recipeId] = fallback;
        return fallback;
    }

    private string GetSolutionItemName(uint recipeId)
        => GetSolutionItemInfo(recipeId).Name;
}
