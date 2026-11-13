using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using GatherBuddy.Crafting;
using GatherBuddy.RebornUI;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;
using ImRaii = Dalamud.Interface.Utility.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private string _solutionsSearch = string.Empty;
    private string? _selectedSolutionKey = null;
    private List<CachedRaphaelSolution> _solutionsList = [];
    private DateTime _solutionsLastRefresh = DateTime.MinValue;
    private readonly Dictionary<uint, (string Name, uint IconId)> _solutionItemCache = [];

    private void DrawSolutionsPage()
    {
        if ((DateTime.UtcNow - _solutionsLastRefresh).TotalSeconds > 0.5)
        {
            _solutionsList = GatherBuddy.RaphaelSolveCoordinator.GetAllCachedSolutions();
            _solutionsList.Sort(static (a, b) =>
            {
                var order = b.GeneratedAt.CompareTo(a.GeneratedAt);
                return order != 0 ? order : string.CompareOrdinal(a.Key, b.Key);
            });
            _solutionsLastRefresh = DateTime.UtcNow;
        }

        var coordinator = GatherBuddy.RaphaelSolveCoordinator;
        var raphaelConfig = GatherBuddy.Config.RaphaelSolverConfig;

        M3Widgets.SearchField("##solutionsSearch", "Search items", ref _solutionsSearch, VulcanUiScaling.Scaled(240f));
        var rowTop = ImGui.GetItemRectMin().Y;
        var rowHeight = ImGui.GetItemRectSize().Y;

        void SameRow(float height)
        {
            ImGui.SameLine(0, M3.Space2);
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, rowTop + (rowHeight - height) * 0.5f));
        }

        SameRow(M3Widgets.ButtonHeight);
        if (M3Widgets.Button("##stopQueue", "Stop queue", M3ButtonStyle.Tonal, FontAwesomeIcon.Stop, enabled: coordinator.PendingSolves > 0,
                tooltip: "Cancel queued and active Raphael solves while keeping completed cached solutions."))
        {
            coordinator.CancelAllPendingSolves();
        }

        SameRow(M3Widgets.ButtonHeight);
        if (M3Widgets.Button("##clearAll", "Clear all", M3ButtonStyle.Text, FontAwesomeIcon.Trash, enabled: _solutionsList.Count > 0,
                tooltip: "Forget every cached solution."))
        {
            coordinator.Clear();
            _selectedSolutionKey = null;
            _solutionsList.Clear();
        }

        SameRow(M3Widgets.CheckboxHitSize().Y);
        var autoClear = raphaelConfig.AutoClearSolutionCache;
        if (M3Widgets.LabeledCheckbox("Clear when a queue starts", ref autoClear,
                tooltip: "Clears cached solutions each time a new crafting queue starts.\nTurn this off to reuse solutions across queue runs when your stats haven't changed."))
        {
            raphaelConfig.AutoClearSolutionCache = autoClear;
            GatherBuddy.Config.Save();
        }

        SameRow(M3Badge.Size("Active 0").Y);
        M3Badge.Draw($"Active {coordinator.ActiveSolves}", coordinator.ActiveSolves > 0 ? M3.Scheme.Success : M3.Scheme.Outline, "Solves running now");
        ImGui.SameLine(0, M3.Space1);
        M3Badge.Draw($"Pending {coordinator.PendingSolves}", coordinator.PendingSolves > 0 ? M3.Scheme.Warning : M3.Scheme.Outline, "Solves waiting in the queue");

        ImGui.Dummy(new Vector2(0, M3.Space2));

        var filtered = _solutionsList;
        if (!string.IsNullOrWhiteSpace(_solutionsSearch))
        {
            filtered = [];
            foreach (var solution in _solutionsList)
            {
                if (GetSolutionItemName(solution.Request.RecipeId).Contains(_solutionsSearch, StringComparison.OrdinalIgnoreCase))
                {
                    filtered.Add(solution);
                }
            }
        }

        if (filtered.Count == 0)
        {
            M3Widgets.EmptyState("##noSolutions", _solutionsList.Count == 0 ? "No cached solutions" : "No matches",
                _solutionsList.Count == 0
                    ? "Start a queue with the Raphael solver to generate solutions."
                    : "No solutions match your search.", FontAwesomeIcon.Lightbulb);
            return;
        }

        var avail = ImGui.GetContentRegionAvail();
        var leftWidth = VulcanUiScaling.Scaled(300f);
        using (var left = M3Panel.Begin("##solLeftPanel", new Vector2(leftWidth, avail.Y), fill: VulcanUiStyle.PanelBackground))
        {
            if (left)
            {
                DrawSolutionsList(filtered);
            }
        }

        ImGui.SameLine(0, M3.Space2);
        using var right = M3Panel.Begin("##solRightPanel", new Vector2(0f, avail.Y));
        if (right)
        {
            DrawSolutionDetail();
        }
    }

    private void DrawSolutionsList(List<CachedRaphaelSolution> solutions)
    {
        var iconSize = VulcanUiScaling.Scaled(28f);
        Ui.ClippedDraw(solutions, solution =>
        {
            var (name, iconId) = GetSolutionItemInfo(solution.Request.RecipeId);
            var req = solution.Request;
            if (Ui.IconListRow($"##sol_{solution.Key}", iconId, name, _selectedSolutionKey == solution.Key, iconSize))
            {
                _selectedSolutionKey = solution.Key;
            }

            M3Tooltip.Hovered($"{name}\nLevel {req.Level}, {req.Craftsmanship}/{req.Control}/{req.CP}, {solution.ActionIds.Count} steps");
        }, iconSize + M3.Space2 + ImGui.GetStyle().ItemSpacing.Y);
    }
    private void DrawSolutionDetail()
    {
        if (_selectedSolutionKey == null)
        {
            M3Widgets.EmptyState("##noSolution", "No solution selected", "Pick a solution on the left to see its steps.", FontAwesomeIcon.Lightbulb);
            return;
        }

        var solution = _solutionsList.Find(s => s.Key == _selectedSolutionKey);
        if (solution == null)
        {
            _selectedSolutionKey = null;
            return;
        }

        var req = solution.Request;
        var (name, iconId) = GetSolutionItemInfo(req.RecipeId);
        var largeIconSize = VulcanUiScaling.Scaled(48f);
        var start = ImGui.GetCursorScreenPos();
        if (iconId > 0)
        {
            M3Widgets.GameIcon(iconId, largeIconSize);
            ImGui.SameLine(0, M3.Space3);
        }

        using (ImRaii.Group())
        {
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, start.Y + (largeIconSize - ImGui.GetTextLineHeightWithSpacing() * 2f) * 0.5f));
            using (ImRaii.PushFont(M3.TitleMedium))
            {
                ImGui.TextUnformatted(name);
            }

            M3Text.Draw($"Recipe {req.RecipeId}, solved {solution.GeneratedAt.ToLocalTime():yyyy-MM-dd HH:mm}", M3TextStyle.Muted);
        }

        ImGui.Dummy(new Vector2(0, M3.Space2));
        using (M3Card.Begin("##solveParameters", "Solve parameters", FontAwesomeIcon.SlidersH, M3.AccentFor(0)))
        {
            M3Widgets.RowValue("Level", req.Level.ToString());
            M3Widgets.RowValue("Craftsmanship", req.Craftsmanship.ToString());
            M3Widgets.RowValue("Control", req.Control.ToString());
            M3Widgets.RowValue("CP", req.CP.ToString());
            M3Widgets.RowValue("Manipulation", req.Manipulation ? "Yes" : "No");
            M3Widgets.RowValue("Specialist", req.Specialist ? "Yes" : "No");
            M3Widgets.RowValue("Initial quality", req.InitialQuality.ToString());
        }

        M3Widgets.SectionLabel($"Actions ({solution.ActionIds.Count})");
        var actionIconSize = VulcanUiScaling.Scaled(24f);
        using var actions = ImRaii.Child("##solActions", new Vector2(-1, ImGui.GetContentRegionAvail().Y), false);
        for (var i = 0; i < solution.ActionIds.Count; i++)
        {
            var actionId = solution.ActionIds[i];
            var skillIconId = GetSkillIconId(actionId);
            var rowStart = ImGui.GetCursorScreenPos();
            if (skillIconId > 0)
            {
                M3Widgets.GameIcon(skillIconId, actionIconSize);
            }
            else
            {
                ImGui.Dummy(new Vector2(actionIconSize));
            }

            ImGui.SameLine(0, M3.Space2);
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, rowStart.Y + (actionIconSize - ImGui.GetTextLineHeight()) * 0.5f));
            M3Text.Draw($"{i + 1}.", M3TextStyle.Faint);
            ImGui.SameLine(0, M3.Space1);
            ImGui.TextUnformatted(((VulcanSkill)actionId).ToString());
        }
    }
    private (string Name, uint IconId) GetSolutionItemInfo(uint recipeId)
    {
        if (_solutionItemCache.TryGetValue(recipeId, out var cached))
        {
            return cached;
        }

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
