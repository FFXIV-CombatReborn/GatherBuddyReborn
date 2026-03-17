using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Vulcan;

public static class CraftingProcessor
{
    private static readonly List<ISolverDefinition> _solverDefinitions = new();
    private static Solver? _activeSolver;
    private static Solver.Recommendation _nextRecommendation;
    private static string _activeSolverName = "";

    public static IReadOnlyList<ISolverDefinition> SolverDefinitions => _solverDefinitions.AsReadOnly();
    public static Solver.Recommendation NextRecommendation => _nextRecommendation;
    public static string ActiveSolverName => _activeSolverName;
    public static bool IsActive => _activeSolver != null;

    public static void Setup()
    {
        _solverDefinitions.Clear();
        _solverDefinitions.Add(new ProgressOnlySolverDefinition());
    }

    public static void Dispose()
    {
        _activeSolver = null;
        _nextRecommendation = new(VulcanSkill.None);
        _activeSolverName = "";
    }

    public static IEnumerable<ISolverDefinition.Desc> GetAvailableSolversForCraft(CraftState craft, bool includeUnsupported = false)
    {
        foreach (var definition in _solverDefinitions)
        {
            foreach (var flavor in definition.Flavors(craft))
            {
                if (includeUnsupported || flavor.UnsupportedReason.Length == 0)
                    yield return flavor;
            }
        }
    }

    public static ISolverDefinition.Desc? FindBestSolver(CraftState craft)
    {
        var available = GetAvailableSolversForCraft(craft, false).ToList();
        return available.Count > 0 ? available.MaxBy(x => x.Priority) : null;
    }

    public static void OnCraftStarted(CraftState craft, StepState initialStep, uint recipeId, bool isTrial)
    {
        GatherBuddy.Log.Debug($"[CraftingProcessor] OnCraftStarted: recipe={recipeId}, solvers available={_solverDefinitions.Count}");
        var bestSolver = FindBestSolver(craft);
        GatherBuddy.Log.Debug($"[CraftingProcessor] FindBestSolver result: {(bestSolver == null ? "null" : bestSolver.Value.Name)}");
        if (bestSolver == null || bestSolver.Value.UnsupportedReason.Length > 0)
        {
            GatherBuddy.Log.Warning($"[CraftingProcessor] No solver available. Reason: {(bestSolver == null ? "null" : bestSolver.Value.UnsupportedReason)}");
            _activeSolver = null;
            _activeSolverName = "";
            return;
        }

        _activeSolver = bestSolver.Value.CreateSolver(craft);
        _activeSolverName = bestSolver.Value.Name;

        if (_activeSolver == null)
        {
            GatherBuddy.Log.Error($"[CraftingProcessor] Failed to create solver instance");
            return;
        }

        _nextRecommendation = _activeSolver.Solve(craft, initialStep);
        GatherBuddy.Log.Debug($"[CraftingProcessor] Initial recommendation: {_nextRecommendation.Action}");
        CraftingEvents.RaiseSolverRecommendationReady(craft, initialStep, _nextRecommendation, _activeSolverName);
    }

    public static void OnCraftAdvanced(CraftState craft, StepState step, uint? recipeId)
    {
        if (_activeSolver == null)
            return;

        _nextRecommendation = _activeSolver.Solve(craft, step);
        CraftingEvents.RaiseSolverRecommendationReady(craft, step, _nextRecommendation, _activeSolverName);
    }

    public static void OnCraftFinished(CraftState craft, StepState finalStep, uint? recipeId, bool cancelled)
    {
        _activeSolver = null;
        _activeSolverName = "";
        _nextRecommendation = new(VulcanSkill.None);
    }

    public static void RegisterSolver(ISolverDefinition definition)
    {
        if (!_solverDefinitions.Any(s => s.GetType() == definition.GetType()))
            _solverDefinitions.Add(definition);
    }

    public static void UnregisterSolver(ISolverDefinition definition)
    {
        _solverDefinitions.RemoveAll(s => s.GetType() == definition.GetType());
    }
}
