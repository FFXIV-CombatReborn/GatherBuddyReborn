using System;
using System.Collections.Generic;
using GatherBuddy.Crafting;

namespace GatherBuddy.Vulcan;

public class RaphaelSolverDefinition : ISolverDefinition
{
    private readonly RaphaelSolveCoordinator _coordinator;

    public RaphaelSolverDefinition(RaphaelSolveCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
    {
        var request = BuildSolveRequest(craft);
        var hasSolution = _coordinator.TryGetSolution(request, out _);
        var inProgress = _coordinator.IsSolveInProgress(request);

        if (hasSolution)
        {
            yield return new(this, 0, 100, "Raphael Solver (Ready)");
        }
        else if (inProgress)
        {
            yield return new(this, 1, 50, "Raphael Solver (Generating...)", "Solution still being generated");
        }
        else
        {
            yield return new(this, 2, 25, "Raphael Solver (Not Ready)", "No solution available");
        }
    }

    public Solver Create(CraftState craft, int flavor)
    {
        var request = BuildSolveRequest(craft);

        if (_coordinator.TryGetSolution(request, out var solution) && solution != null)
        {
            GatherBuddy.Log.Debug($"[RaphaelSolverDefinition] Using cached Raphael solution for recipe {craft.RecipeId}");
            return new RaphaelMacroSolver(solution, craft);
        }

        GatherBuddy.Log.Debug($"[RaphaelSolverDefinition] No Raphael solution ready for recipe {craft.RecipeId}, falling back");
        return null!;
    }

    private RaphaelSolveRequest BuildSolveRequest(CraftState craft)
    {
        return new RaphaelSolveRequest(
            RecipeId: craft.RecipeId,
            Level: craft.StatLevel,
            Craftsmanship: craft.StatCraftsmanship,
            Control: craft.StatControl,
            CP: craft.StatCP,
            Manipulation: craft.UnlockedManipulation,
            Specialist: craft.Specialist,
            InitialQuality: craft.InitialQuality
        );
    }
}

public class RaphaelMacroSolver : Solver
{
    private readonly CachedRaphaelSolution _solution;
    private readonly CraftState _craft;
    private int _currentActionIndex = 0;

    public RaphaelMacroSolver(CachedRaphaelSolution solution, CraftState craft)
    {
        _solution = solution ?? throw new ArgumentNullException(nameof(solution));
        _craft = craft ?? throw new ArgumentNullException(nameof(craft));
    }

    public override Recommendation Solve(CraftState craft, StepState step)
    {
        if (_currentActionIndex >= _solution.ActionIds.Count)
        {
            GatherBuddy.Log.Debug($"[RaphaelMacroSolver] All Raphael actions completed");
            return new(VulcanSkill.None, "Raphael solution complete");
        }

        var actionId = _solution.ActionIds[_currentActionIndex];
        var vulcanSkill = ConvertActionIdToSkill(actionId);

        _currentActionIndex++;

        var progress = _currentActionIndex * 100 / _solution.ActionIds.Count;
        return new(vulcanSkill, $"Raphael step {_currentActionIndex}/{_solution.ActionIds.Count} ({progress}%)");
    }

    private VulcanSkill ConvertActionIdToSkill(uint actionId)
    {
        return (VulcanSkill)actionId;
    }

    public override Solver Clone()
    {
        var cloned = (RaphaelMacroSolver)MemberwiseClone();
        cloned._currentActionIndex = 0;
        return cloned;
    }
}
