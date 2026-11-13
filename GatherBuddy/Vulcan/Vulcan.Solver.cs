using System.Collections.Generic;

namespace GatherBuddy.Vulcan;

public interface ISolverDefinition
{
    record struct Desc(ISolverDefinition Def, int Flavor, int Priority, string Name, string UnsupportedReason = "")
    {
        public Solver? CreateSolver(CraftState craft)
        {
            return this == default ? null : Def.Create(craft, Flavor);
        }
    }

    IEnumerable<Desc> Flavors(CraftState craft);
    Solver Create(CraftState craft, int flavor);
}

public abstract class Solver
{
    public record struct Recommendation(VulcanSkill Action, string Comment = "");

    public virtual Solver Clone() => (Solver)MemberwiseClone();
    public abstract Recommendation Solve(CraftState craft, StepState step);
}

public interface ICraftValidator
{
    bool Validate(CraftState craft);
}

public readonly struct SolverRef
{
    public string Name { get; private init; } = "";
    private readonly Solver? _solver;

    public SolverRef(string name, Solver? solver = null)
    {
        Name = name;
        _solver = solver;
    }

    public Solver? Clone() => _solver?.Clone();
    public bool IsType<T>() where T : Solver => _solver is T;

    public static implicit operator bool(SolverRef x) => x._solver != null;
}
