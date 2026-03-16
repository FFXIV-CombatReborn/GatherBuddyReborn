using System.Collections.Generic;

namespace GatherBuddy.Vulcan;

public class ProgressOnlySolverDefinition : ISolverDefinition
{
    public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
    {
        if (!craft.CraftExpert && !craft.CraftCollectible)
            yield return new(this, 0, 1, "Progress Only Solver");
    }

    public Solver Create(CraftState craft, int flavor) => new ProgressOnlySolver();
}

public class ProgressOnlySolver : Solver
{
    public override Recommendation Solve(CraftState craft, StepState step)
    {
        if (Simulator.CanUseAction(craft, step, VulcanSkill.MuscleMemory))
            return new(VulcanSkill.MuscleMemory);

        if (step.VenerationLeft == 0 && Simulator.CanUseAction(craft, step, VulcanSkill.Veneration))
            return new(VulcanSkill.Veneration);

        var synthOption = BestSynthesis(craft, step, true);
        if (Simulator.GetDurabilityCost(step, synthOption) >= step.Durability)
        {
            if (Simulator.CanUseAction(craft, step, VulcanSkill.ImmaculateMend) && craft.CraftDurability >= 70)
                return new(VulcanSkill.ImmaculateMend);
            if (Simulator.CanUseAction(craft, step, VulcanSkill.MastersMend))
                return new(VulcanSkill.MastersMend);
        }

        return new(synthOption);
    }

    private static VulcanSkill BestSynthesis(CraftState craft, StepState step, bool progOnly = false)
    {
        var remainingProgress = craft.CraftProgress - step.Progress;

        if (Simulator.CalculateProgress(craft, step, VulcanSkill.BasicSynthesis) >= remainingProgress)
            return VulcanSkill.BasicSynthesis;

        if (Simulator.CanUseAction(craft, step, VulcanSkill.IntensiveSynthesis))
            return VulcanSkill.IntensiveSynthesis;

        if (Simulator.CanUseAction(craft, step, VulcanSkill.Groundwork) && step.Durability > Simulator.GetDurabilityCost(step, VulcanSkill.Groundwork))
            return VulcanSkill.Groundwork;

        if (Simulator.CanUseAction(craft, step, VulcanSkill.PrudentSynthesis))
            return VulcanSkill.PrudentSynthesis;

        if (Simulator.CanUseAction(craft, step, VulcanSkill.CarefulSynthesis))
            return VulcanSkill.CarefulSynthesis;

        return VulcanSkill.BasicSynthesis;
    }
}
