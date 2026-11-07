namespace GatherBuddy.Vulcan;

public static class SolverUtils
{
    private const int MaxEstimatedActions = 200;
    public enum CraftStatus
    {
        InProgress,
        Complete,
        Failed
    }

    public static CraftStatus Status(CraftState craft, StepState step)
    {
        if (step.Durability <= 0)
        {
            return CraftStatus.Failed;
        }

        if (step.Progress >= craft.CraftProgress)
        {
            return CraftStatus.Complete;
        }

        return CraftStatus.InProgress;
    }

    public static StepState CreateInitial(CraftState craft, int startingQuality = 0)
        => GameStateBuilder.BuildInitialStepState(craft, startingQuality);

    public static StepState? SimulateSolverExecution(Solver csolver, CraftState craft, int startingQuality)
    {
        var solver = csolver.Clone();
        var step = CreateInitial(craft, startingQuality);
        while (Status(craft, step) == CraftStatus.InProgress)
        {
            var rec = solver.Solve(craft, step);
            var action = rec.Action;
            if (action == VulcanSkill.None)
            {
                return null;
            }

            var (res, next) = Simulator.Execute(craft, step, action, 0, 1);
            if (res == Simulator.ExecuteResult.CantUse)
            {
                return null;
            }

            step = next;
        }
        return step;
    }

    public static bool TryEstimateActionCount(Solver csolver, CraftState craft, int startingQuality, out int actionCount)
    {
        actionCount = 0;

        var solver = csolver.Clone();
        var step = CreateInitial(craft, startingQuality);
        while (Status(craft, step) == CraftStatus.InProgress)
        {
            if (actionCount >= MaxEstimatedActions)
            {
                return false;
            }

            var action = solver.Solve(craft, step).Action;
            if (action == VulcanSkill.None)
            {
                return false;
            }

            var (res, next) = Simulator.Execute(craft, step, action, 0, 1);
            if (res == Simulator.ExecuteResult.CantUse)
            {
                return false;
            }

            actionCount++;
            step = next;
        }

        return Status(craft, step) == CraftStatus.Complete;
    }

    public static double EstimateQualityPercent(Solver solver, CraftState craft, int startingQuality)
    {
        var res = SimulateSolverExecution(solver, craft, startingQuality);
        return res != null ? res.Quality * 100.0 / craft.CraftQualityMax : 0;
    }

    public static bool EstimateProgressChance(Solver solver, CraftState craft, int startingQuality)
    {
        var res = SimulateSolverExecution(solver, craft, startingQuality);
        return res != null && res.Progress >= craft.CraftProgress;
    }

    public static string EstimateCollectibleThreshold(Solver solver, CraftState craft, int startingQuality)
    {
        var res = SimulateSolverExecution(solver, craft, startingQuality);
        var finalBreakpoint = craft.CraftQualityMin2 != craft.CraftQualityMin1 ? "3rd" : "2nd";
        return res == null || res.Quality < craft.CraftQualityMin1 || res.Progress < craft.CraftProgress ? "Fail" : res.Quality >= craft.CraftQualityMin3 ? $"{finalBreakpoint}" : res.Quality >= craft.CraftQualityMin2 && craft.CraftQualityMin2 != craft.CraftQualityMin1 ? "2nd" : "1st";
    }
}
