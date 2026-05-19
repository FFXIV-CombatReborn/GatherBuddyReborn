using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Crafting;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Vulcan.Workshops;

internal enum WorkshopScopeKind
{
    Project,
    Part,
    Phase,
}

internal sealed record WorkshopSupplyRequirement(
    uint ItemId,
    string ItemName,
    uint IconId,
    int RequiredQuantity,
    uint? RecipeId,
    int CraftsNeeded)
{
    public bool IsCraftable
        => RecipeId.HasValue;
}

internal abstract class WorkshopScopeNode
{
    protected WorkshopScopeNode(uint projectId, string projectName, uint projectIconId, List<WorkshopSupplyRequirement> requirements)
    {
        ProjectId = projectId;
        ProjectName = projectName;
        ProjectIconId = projectIconId;
        Requirements = requirements;
    }

    public uint ProjectId { get; }
    public string ProjectName { get; }
    public uint ProjectIconId { get; }
    public List<WorkshopSupplyRequirement> Requirements { get; }
    public abstract WorkshopScopeKind Kind { get; }
    public abstract string DisplayName { get; }
    public int CraftableRequirementCount => Requirements.Count(r => r.IsCraftable);
    public int UncraftableRequirementCount => Requirements.Count - CraftableRequirementCount;
    public int TotalRequiredItemCount => Requirements.Sum(r => r.RequiredQuantity);
    public int TotalCraftsNeeded => Requirements.Where(r => r.IsCraftable).Sum(r => r.CraftsNeeded);
}

internal sealed class WorkshopProjectNode : WorkshopScopeNode
{
    public WorkshopProjectNode(uint projectId, string projectName, uint projectIconId, List<WorkshopSupplyRequirement> requirements)
        : base(projectId, projectName, projectIconId, requirements)
    { }

    public uint SequenceId => ProjectId;
    public List<WorkshopPartNode> Parts { get; } = [];
    public int PhaseCount => Parts.Sum(part => part.Phases.Count);
    public override WorkshopScopeKind Kind => WorkshopScopeKind.Project;
    public override string DisplayName => ProjectName;
}

internal sealed class WorkshopPartNode : WorkshopScopeNode
{
    public WorkshopPartNode(
        uint projectId,
        string projectName,
        uint projectIconId,
        uint partId,
        string partName,
        int partIndex,
        List<WorkshopSupplyRequirement> requirements)
        : base(projectId, projectName, projectIconId, requirements)
    {
        PartId = partId;
        PartName = partName;
        PartIndex = partIndex;
    }

    public uint PartId { get; }
    public string PartName { get; }
    public int PartIndex { get; }
    public List<WorkshopPhaseNode> Phases { get; } = [];
    public override WorkshopScopeKind Kind => WorkshopScopeKind.Part;
    public override string DisplayName => $"Part {PartIndex}: {PartName}";
}

internal sealed class WorkshopPhaseNode : WorkshopScopeNode
{
    public WorkshopPhaseNode(
        uint projectId,
        string projectName,
        uint projectIconId,
        uint partId,
        string partName,
        int partIndex,
        uint phaseId,
        int phaseIndex,
        List<WorkshopSupplyRequirement> requirements)
        : base(projectId, projectName, projectIconId, requirements)
    {
        PartId = partId;
        PartName = partName;
        PartIndex = partIndex;
        PhaseId = phaseId;
        PhaseIndex = phaseIndex;
    }

    public uint PartId { get; }
    public string PartName { get; }
    public int PartIndex { get; }
    public uint PhaseId { get; }
    public int PhaseIndex { get; }
    public override WorkshopScopeKind Kind => WorkshopScopeKind.Phase;
    public override string DisplayName => $"{PartName} - Phase {PhaseIndex}";
}

internal sealed record WorkshopListDraft(string Name, string Description, IReadOnlyList<(uint RecipeId, int Quantity)> Recipes);

internal static class WorkshopDataService
{
    private static List<WorkshopProjectNode>? _cachedProjects;
    private static Dictionary<uint, (uint RecipeId, int AmountResult)>? _recipeLookup;

    public static IReadOnlyList<WorkshopProjectNode> GetProjects()
    {
        if (_cachedProjects != null)
            return _cachedProjects;

        _recipeLookup = BuildRecipeLookup();
        _cachedProjects = LoadProjects(_recipeLookup);
        return _cachedProjects;
    }

    public static WorkshopListDraft? CreateListDraft(WorkshopScopeNode scope, int loopCount)
    {
        var normalizedLoopCount = Math.Max(1, loopCount);
        var recipes = scope.Requirements
            .Where(requirement => requirement.IsCraftable)
            .GroupBy(requirement => requirement.RecipeId!.Value)
            .Select(group => (RecipeId: group.Key, Quantity: group.Sum(requirement => checked(requirement.CraftsNeeded * normalizedLoopCount))))
            .Where(recipe => recipe.Quantity > 0)
            .OrderBy(recipe => recipe.RecipeId)
            .ToList();

        if (recipes.Count == 0)
        {
            GatherBuddy.Log.Debug($"[WorkshopDataService] No craftable recipes found for {scope.Kind} '{scope.DisplayName}'");
            return null;
        }

        var draft = new WorkshopListDraft(
            BuildListName(scope, normalizedLoopCount),
            BuildDescription(scope, normalizedLoopCount),
            recipes);
        GatherBuddy.Log.Debug(
            $"[WorkshopDataService] Built workshop draft '{draft.Name}' with {draft.Recipes.Count} recipe(s), loops={normalizedLoopCount}, skipped={scope.UncraftableRequirementCount}");
        return draft;
    }

    private static List<WorkshopProjectNode> LoadProjects(Dictionary<uint, (uint RecipeId, int AmountResult)> recipeLookup)
    {
        var sequenceSheet = Dalamud.GameData.GetExcelSheet<CompanyCraftSequence>();
        if (sequenceSheet == null)
        {
            GatherBuddy.Log.Warning("[WorkshopDataService] CompanyCraftSequence sheet unavailable; FC Workshops tab will be empty");
            return [];
        }

        var projects = new List<WorkshopProjectNode>();
        foreach (var sequence in sequenceSheet)
        {
            if (sequence.RowId == 0 || sequence.ResultItem.RowId == 0)
                continue;

            var projectName = GetProjectName(sequence);
            var projectIconId = GetProjectIconId(sequence);
            var partNodes = new List<WorkshopPartNode>();
            var projectRequirements = new List<(uint ItemId, int Quantity)>();
            var partIndex = 1;

            foreach (var part in EnumerateParts(sequence))
            {
                var partName = GetPartName(part, partIndex);
                var phaseNodes = new List<WorkshopPhaseNode>();
                var partRequirements = new List<(uint ItemId, int Quantity)>();
                var phaseIndex = 1;

                foreach (var phase in EnumerateProcesses(part))
                {
                    var phaseRequirements = EnumeratePhaseRequirements(phase).ToList();
                    if (phaseRequirements.Count == 0)
                    {
                        phaseIndex++;
                        continue;
                    }

                    partRequirements.AddRange(phaseRequirements);
                    projectRequirements.AddRange(phaseRequirements);
                    phaseNodes.Add(new WorkshopPhaseNode(
                        sequence.RowId,
                        projectName,
                        projectIconId,
                        part.RowId,
                        partName,
                        partIndex,
                        phase.RowId,
                        phaseIndex,
                        BuildRequirements(phaseRequirements, recipeLookup)));
                    phaseIndex++;
                }

                if (partRequirements.Count == 0)
                {
                    partIndex++;
                    continue;
                }

                var partNode = new WorkshopPartNode(
                    sequence.RowId,
                    projectName,
                    projectIconId,
                    part.RowId,
                    partName,
                    partIndex,
                    BuildRequirements(partRequirements, recipeLookup));
                partNode.Phases.AddRange(phaseNodes);
                partNodes.Add(partNode);
                partIndex++;
            }

            if (projectRequirements.Count == 0)
                continue;

            var projectNode = new WorkshopProjectNode(
                sequence.RowId,
                projectName,
                projectIconId,
                BuildRequirements(projectRequirements, recipeLookup));
            projectNode.Parts.AddRange(partNodes);
            projects.Add(projectNode);
        }

        projects.Sort((left, right) => string.Compare(left.ProjectName, right.ProjectName, StringComparison.OrdinalIgnoreCase));
        GatherBuddy.Log.Debug($"[WorkshopDataService] Loaded {projects.Count} FC workshop project(s)");
        return projects;
    }

    private static Dictionary<uint, (uint RecipeId, int AmountResult)> BuildRecipeLookup()
    {
        var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (recipeSheet == null)
        {
            GatherBuddy.Log.Warning("[WorkshopDataService] Recipe sheet unavailable; workshop craft resolution disabled");
            return [];
        }

        var lookup = new Dictionary<uint, (uint RecipeId, int AmountResult)>();
        foreach (var recipe in recipeSheet)
        {
            if (recipe.RowId == 0 || recipe.ItemResult.RowId == 0 || lookup.ContainsKey(recipe.ItemResult.RowId))
                continue;

            lookup[recipe.ItemResult.RowId] = (recipe.RowId, Math.Max(1, (int)recipe.AmountResult));
        }

        return lookup;
    }

    private static IEnumerable<CompanyCraftPart> EnumerateParts(CompanyCraftSequence sequence)
    {
        for (var i = 0; i < sequence.CompanyCraftPart.Count; i++)
        {
            var part = sequence.CompanyCraftPart[i].Value;
            if (part.RowId > 0)
                yield return part;
        }
    }

    private static IEnumerable<CompanyCraftProcess> EnumerateProcesses(CompanyCraftPart part)
    {
        for (var i = 0; i < part.CompanyCraftProcess.Count; i++)
        {
            var process = part.CompanyCraftProcess[i].Value;
            if (process.RowId > 0)
                yield return process;
        }
    }

    private static IEnumerable<(uint ItemId, int Quantity)> EnumeratePhaseRequirements(CompanyCraftProcess process)
    {
        var supplyCount = Math.Min(process.SupplyItem.Count, Math.Min(process.SetsRequired.Count, process.SetQuantity.Count));
        for (var i = 0; i < supplyCount; i++)
        {
            var supplyItem = process.SupplyItem[i].Value;
            if (supplyItem.RowId == 0 || supplyItem.Item.RowId == 0)
                continue;

            var requiredQuantity = process.SetsRequired[i] * process.SetQuantity[i];
            if (requiredQuantity <= 0)
                continue;

            yield return (supplyItem.Item.RowId, requiredQuantity);
        }
    }

    private static List<WorkshopSupplyRequirement> BuildRequirements(
        IEnumerable<(uint ItemId, int Quantity)> rawRequirements,
        Dictionary<uint, (uint RecipeId, int AmountResult)> recipeLookup)
        => rawRequirements
            .Where(requirement => requirement.ItemId > 0 && requirement.Quantity > 0)
            .GroupBy(requirement => requirement.ItemId)
            .Select(group =>
            {
                var requiredQuantity = group.Sum(requirement => requirement.Quantity);
                var (itemName, iconId) = GetItemDisplay(group.Key);
                if (recipeLookup.TryGetValue(group.Key, out var recipeInfo))
                {
                    return new WorkshopSupplyRequirement(
                        group.Key,
                        itemName,
                        iconId,
                        requiredQuantity,
                        recipeInfo.RecipeId,
                        DivideRoundUp(requiredQuantity, recipeInfo.AmountResult));
                }

                return new WorkshopSupplyRequirement(group.Key, itemName, iconId, requiredQuantity, null, 0);
            })
            .OrderBy(requirement => requirement.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string GetProjectName(CompanyCraftSequence sequence)
    {
        var item = sequence.ResultItem.Value;
        return item.RowId > 0
            ? item.Name.ExtractText()
            : $"Project {sequence.RowId}";
    }

    private static uint GetProjectIconId(CompanyCraftSequence sequence)
    {
        var item = sequence.ResultItem.Value;
        return item.RowId > 0 ? (uint)item.Icon : 0;
    }

    private static string GetPartName(CompanyCraftPart part, int partIndex)
    {
        var partName = part.CompanyCraftType.Value.RowId > 0
            ? part.CompanyCraftType.Value.Name.ExtractText()
            : string.Empty;
        return string.IsNullOrWhiteSpace(partName)
            ? $"Part {partIndex}"
            : partName;
    }

    private static (string Name, uint IconId) GetItemDisplay(uint itemId)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        if (itemSheet != null && itemSheet.TryGetRow(itemId, out var item))
            return (item.Name.ExtractText(), (uint)item.Icon);

        GatherBuddy.Log.Debug($"[WorkshopDataService] Missing item metadata for workshop supply item {itemId}");
        return ($"Item {itemId}", 0);
    }

    internal static string GetDefaultListName(WorkshopScopeNode scope, int loopCount)
        => BuildListName(scope, Math.Max(1, loopCount));

    private static string BuildListName(WorkshopScopeNode scope, int loopCount)
        => scope switch
        {
            WorkshopProjectNode project => $"{project.ProjectName} x{loopCount}",
            WorkshopPartNode part => $"{part.ProjectName} - {part.PartName} x{loopCount}",
            WorkshopPhaseNode phase => $"{phase.ProjectName} - {phase.PartName}, Phase {phase.PhaseIndex} x{loopCount}",
            _ => $"{scope.ProjectName} x{loopCount}",
        };

    private static string BuildDescription(WorkshopScopeNode scope, int loopCount)
        => $"{scope.DisplayName} x{loopCount}";

    private static int DivideRoundUp(int value, int divisor)
        => (int)Math.Ceiling((double)value / Math.Max(1, divisor));
}
