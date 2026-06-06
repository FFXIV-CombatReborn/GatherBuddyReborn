using System.Collections.Generic;

namespace GatherBuddy.Vulcan;

/// knobs for cosmic crafting (Material Miracle stays opt-in until the sim/solver is tuned in-game)
public class CosmicSolverConfig
{
    public bool UseMaterialMiracle       = true;
    public int  MinimumStepsBeforeMiracle = 2;
    public bool MaterialMiracleMulti     = false;
    public int  MaxSteadyUses            = 1;
    public int  CollectibleMode          = 3; // 1 bronze / 2 silver / 3 gold
}

public class CosmicExpertSolverDefinition : ISolverDefinition
{
    private readonly CosmicSolverConfig _config;

    public CosmicExpertSolverDefinition(CosmicSolverConfig config) => _config = config;

    public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
    {
        // only cosmic crafts; high priority so it wins over the cosmic-incapable Raphael/Standard paths
        if (craft.IsCosmic)
            yield return new(this, 0, 200, "Cosmic Solver");
    }

    public Solver Create(CraftState craft, int flavor) => new CosmicExpertSolver(_config);
}

/// cosmic layer over the standard solver: adds Steady Hand handling and expert-condition progress,
/// then hands quality/IQ/Byregot/Material-Miracle/durability decisions back to the standard solver
public class CosmicExpertSolver : Solver
{
    private readonly CosmicSolverConfig _config;
    private readonly StandardSolver     _core;

    public CosmicExpertSolver(CosmicSolverConfig config)
    {
        _config = config;
        _core = new StandardSolver(new StandardSolverConfig
        {
            SolverCollectibleMode     = config.CollectibleMode,
            UseMaterialMiracle        = config.UseMaterialMiracle,
            MinimumStepsBeforeMiracle = config.MinimumStepsBeforeMiracle,
            MaterialMiracleMulti      = config.MaterialMiracleMulti,
            UseSpecialist             = false,
        });
    }

    public override Solver Clone() => new CosmicExpertSolver(_config);

    public override Recommendation Solve(CraftState craft, StepState step)
    {
        // the standard solver owns the progress/quality balance (incl. Material Miracle + Final Appraisal)
        var rec = _core.Solve(craft, step);

        // only layer Steady Hand on top, and only while it's already doing progress - never override a
        // quality decision and never overshoot into finishing the craft early (that's what killed quality)
        if (!craft.MissionHasSteadyHand)
            return rec;

        var progressLeft = craft.CraftProgress - step.Progress;
        if (progressLeft <= 0 || !IsSynthesis(rec.Action))
            return rec;

        if (step.SteadyHandLeft > 0
         && step.Durability > Simulator.GetDurabilityCost(step, VulcanSkill.RapidSynthesis)
         && Simulator.CanUseAction(craft, step, VulcanSkill.RapidSynthesis)
         && !WouldFinish(craft, step, VulcanSkill.RapidSynthesis))
            return new(VulcanSkill.RapidSynthesis, "steady hand rapid");

        // apply Steady Hand only inside the Muscle Memory window so its guaranteed Rapids ride the buff;
        // applying it on the opener just burns the charge on un-buffed synths
        if (step.MuscleMemoryLeft > 0 && step.SteadyHandLeft == 0
         && step.SteadyHandsUsed < _config.MaxSteadyUses && step.WasteNotLeft == 0
         && Simulator.CanUseAction(craft, step, VulcanSkill.SteadyHand))
        {
            // get Veneration up first so the Rapids hit harder
            if (step.VenerationLeft == 0 && step.MuscleMemoryLeft > 2
             && Simulator.CanUseAction(craft, step, VulcanSkill.Veneration))
                return new(VulcanSkill.Veneration, "pre-steady veneration");
            return new(VulcanSkill.SteadyHand, "steady hand");
        }

        return rec;
    }

    private static bool IsSynthesis(VulcanSkill a) => a is
        VulcanSkill.BasicSynthesis or VulcanSkill.CarefulSynthesis or VulcanSkill.RapidSynthesis or
        VulcanSkill.IntensiveSynthesis or VulcanSkill.Groundwork or VulcanSkill.PrudentSynthesis or
        VulcanSkill.MuscleMemory or VulcanSkill.DelicateSynthesis;

    private static bool WouldFinish(CraftState craft, StepState step, VulcanSkill action)
        => step.FinalAppraisalLeft == 0
        && step.Progress + Simulator.CalculateProgress(craft, step, action) >= craft.CraftProgress;
}
