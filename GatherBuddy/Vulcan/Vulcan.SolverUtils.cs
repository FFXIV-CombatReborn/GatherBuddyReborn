namespace GatherBuddy.Vulcan;

public static class SolverUtils
{
    public enum CraftStatus
    {
        InProgress,
        Complete,
        Failed
    }

    public static CraftStatus Status(CraftState craft, StepState step)
    {
        if (step.Durability <= 0)
            return CraftStatus.Failed;
        if (step.Progress >= craft.CraftProgress)
            return CraftStatus.Complete;
        return CraftStatus.InProgress;
    }

    public static StepState CreateInitial(CraftState craft, int startingQuality = 0)
    {
        return new StepState
        {
            Index = 1,
            Progress = 0,
            Quality = startingQuality,
            Durability = craft.CraftDurability,
            RemainingCP = craft.StatCP,
            Condition = Condition.Normal,
            IQStacks = 0,
            WasteNotLeft = 0,
            ManipulationLeft = 0,
            GreatStridesLeft = 0,
            InnovationLeft = 0,
            VenerationLeft = 0,
            MuscleMemoryLeft = 0,
            FinalAppraisalLeft = 0,
            CarefulObservationLeft = craft.Specialist ? 2 : 0,
            HeartAndSoulActive = false,
            HeartAndSoulAvailable = craft.Specialist,
            PrevActionFailed = false,
            PrevComboAction = VulcanSkill.None,
            ExpedienceLeft = 0,
            QuickInnoLeft = 0,
            QuickInnoAvailable = false,
            TrainedPerfectionAvailable = false,
            TrainedPerfectionActive = false,
            MaterialMiracleCharges = craft.MissionHasMaterialMiracle ? 1u : 0u,
            MaterialMiracleActive = false,
            ObserveCounter = 0
        };
    }

    public static StepState? SimulateSolverExecution(Solver csolver, CraftState craft, int startingQuality)
    {
        var solver = csolver.Clone();
        var step = CreateInitial(craft, startingQuality);
        while (Status(craft, step) == CraftStatus.InProgress)
        {
            var rec = solver.Solve(craft, step);
            var action = rec.Action;
            if (action == VulcanSkill.None)
                return null;

            var (res, next) = Simulator.Execute(craft, step, action, 0, 1);
            if (res == Simulator.ExecuteResult.CantUse)
                return null;

            step = next;
        }
        return step;
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
        string finalBreakpoint = craft.CraftQualityMin2 != craft.CraftQualityMin1 ? "3rd" : "2nd";
        return res == null || res.Quality < craft.CraftQualityMin1 || res.Progress < craft.CraftProgress ? "Fail" : res.Quality >= craft.CraftQualityMin3 ? $"{finalBreakpoint}" : res.Quality >= craft.CraftQualityMin2 && craft.CraftQualityMin2 != craft.CraftQualityMin1 ? "2nd" : "1st";
    }
}
