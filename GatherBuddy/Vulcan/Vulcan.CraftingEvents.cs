namespace GatherBuddy.Vulcan;

public static class CraftingEvents
{
    public delegate void CraftStartedDelegate(CraftState craft, StepState initialStep, uint recipeId, bool isTrial);
    public static event CraftStartedDelegate? CraftStarted;

    public delegate void CraftAdvancedDelegate(CraftState craft, StepState step, uint? recipeId);
    public static event CraftAdvancedDelegate? CraftAdvanced;

    public delegate void CraftFinishedDelegate(CraftState craft, StepState finalStep, uint? recipeId, bool cancelled);
    public static event CraftFinishedDelegate? CraftFinished;

    public delegate void SolverRecommendationReadyDelegate(CraftState craft, StepState step, Solver.Recommendation recommendation, string solverName);
    public static event SolverRecommendationReadyDelegate? SolverRecommendationReady;

    public static void RaiseCraftStarted(CraftState craft, StepState initialStep, uint recipeId, bool isTrial)
        => CraftStarted?.Invoke(craft, initialStep, recipeId, isTrial);

    public static void RaiseCraftAdvanced(CraftState craft, StepState step, uint? recipeId)
        => CraftAdvanced?.Invoke(craft, step, recipeId);

    public static void RaiseCraftFinished(CraftState craft, StepState finalStep, uint? recipeId, bool cancelled)
        => CraftFinished?.Invoke(craft, finalStep, recipeId, cancelled);

    public static void RaiseSolverRecommendationReady(CraftState craft, StepState step, Solver.Recommendation recommendation, string solverName)
        => SolverRecommendationReady?.Invoke(craft, step, recommendation, solverName);
}
