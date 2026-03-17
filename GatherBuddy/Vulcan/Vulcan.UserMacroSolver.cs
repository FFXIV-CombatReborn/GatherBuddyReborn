using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using GatherBuddy.Crafting;

namespace GatherBuddy.Vulcan;

public class UserMacroSolverDefinition : ISolverDefinition
{
    private readonly UserMacroLibrary _macroLibrary;

    public UserMacroSolverDefinition(UserMacroLibrary macroLibrary)
    {
        _macroLibrary = macroLibrary ?? throw new ArgumentNullException(nameof(macroLibrary));
    }

    public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
    {
        var selectedMacroId = CraftingGameInterop.GetSelectedMacro();
        
        if (!string.IsNullOrEmpty(selectedMacroId))
        {
            var selectedMacro = _macroLibrary.GetMacroByStringId(selectedMacroId);
            if (selectedMacro != null)
            {
                var statsOk = selectedMacro.MinCraftsmanship <= craft.StatCraftsmanship 
                           && selectedMacro.MinControl <= craft.StatControl 
                           && selectedMacro.MinCP <= craft.StatCP;
                
                var flavorId = selectedMacro.Id.GetHashCode();
                var priority = statsOk ? 100 : 95;
                var warningMsg = statsOk ? "" : "You do not meet the minimum stats for this macro";
                
                GatherBuddy.Log.Debug($"[UserMacroSolver] Using selected macro '{selectedMacro.Name}' for recipe {craft.RecipeId}");
                yield return new(this, flavorId, priority, $"User Macro: {selectedMacro.Name}", warningMsg);
                yield break;
            }
            else
            {
                GatherBuddy.Log.Warning($"[UserMacroSolver] Selected macro '{selectedMacroId}' not found");
            }
        }
        
        var macros = _macroLibrary.GetMacrosForRecipe(craft.RecipeId);
        
        if (macros.Count == 0)
        {
            yield break;
        }

        foreach (var macro in macros)
        {
            var statsOk = macro.MinCraftsmanship <= craft.StatCraftsmanship 
                       && macro.MinControl <= craft.StatControl 
                       && macro.MinCP <= craft.StatCP;
            
            var flavorId = macro.Id.GetHashCode();
            var priority = statsOk ? 90 : 10;
            var warningMsg = statsOk ? "" : "You do not meet the minimum stats for this macro";
            
            yield return new(this, flavorId, priority, $"User Macro: {macro.Name}", warningMsg);
        }
    }

    public Solver Create(CraftState craft, int flavor)
    {
        var macro = _macroLibrary.GetMacroById(flavor);
        if (macro == null)
        {
            GatherBuddy.Log.Warning($"[UserMacroSolver] Could not find macro with id {flavor}");
            return null!;
        }

        GatherBuddy.Log.Debug($"[UserMacroSolver] Using user macro '{macro.Name}' for recipe {craft.RecipeId}");
        return new UserMacroSolver(macro, craft);
    }
}

public class UserMacroSolver : Solver
{
    private readonly UserMacro  _macro;
    private readonly CraftState _craft;
    private int     _currentActionIndex = 0;
    private Solver? _fallback;

    public UserMacroSolver(UserMacro macro, CraftState craft)
    {
        _macro = macro ?? throw new ArgumentNullException(nameof(macro));
        _craft = craft ?? throw new ArgumentNullException(nameof(craft));

        var fallbackDesc = CraftingProcessor.GetAvailableSolversForCraft(craft)
            .Where(d => d.Def is not UserMacroSolverDefinition)
            .MaxBy(d => d.Priority);
        _fallback = fallbackDesc == default ? null : fallbackDesc.CreateSolver(craft);
        GatherBuddy.Log.Debug($"[UserMacroSolver] Fallback solver: {(fallbackDesc == default ? "none" : fallbackDesc.Name)}");
    }

    public override Recommendation Solve(CraftState craft, StepState step)
    {
        var fallback = _fallback?.Solve(craft, step);

        while (_currentActionIndex < _macro.Actions.Count)
        {
            var skill = ConvertActionIdToSkill(_macro.Actions[_currentActionIndex++]);

            if (GatherBuddy.Config.SkipMacroStepIfUnable && !Simulator.CanUseAction(craft, step, skill))
            {
                GatherBuddy.Log.Debug($"[UserMacroSolver] Skipping unusable action {skill} at step {_currentActionIndex}");
                continue;
            }

            var progress = _currentActionIndex * 100 / _macro.Actions.Count;
            return new(skill, $"{_macro.Name} step {_currentActionIndex}/{_macro.Actions.Count} ({progress}%)");
        }

        if (GatherBuddy.Config.MacroFallbackEnabled && fallback.HasValue && fallback.Value.Action != VulcanSkill.None)
        {
            GatherBuddy.Log.Debug($"[UserMacroSolver] Macro complete, falling back: {fallback.Value.Action}");
            return new(fallback.Value.Action, $"Macro complete — {fallback.Value.Comment}");
        }

        GatherBuddy.Log.Debug("[UserMacroSolver] All macro actions completed");
        return new(VulcanSkill.None, "Macro complete");
    }

    private static VulcanSkill ConvertActionIdToSkill(uint actionId)
        => (VulcanSkill)actionId;

    public override Solver Clone()
    {
        var cloned = (UserMacroSolver)MemberwiseClone();
        cloned._currentActionIndex = 0;
        cloned._fallback = _fallback?.Clone();
        return cloned;
    }
}

public class UserMacro
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Unnamed Macro";
    public List<uint> Actions { get; set; } = new();
    public int MinCraftsmanship { get; set; }
    public int MinControl { get; set; }
    public int MinCP { get; set; }
    public string Source { get; set; } = "Manual";
    public string? TeamcraftUrl { get; set; }
    public string? Author { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class UserMacroLibrary
{
    private readonly List<UserMacro> _macros = new();
    private readonly Dictionary<uint, List<UserMacro>> _macrosByRecipe = new();

    public void AddMacro(UserMacro macro, uint recipeId = 0)
    {
        _macros.Add(macro);
        
        if (recipeId > 0)
        {
            if (!_macrosByRecipe.ContainsKey(recipeId))
                _macrosByRecipe[recipeId] = new();
            
            _macrosByRecipe[recipeId].Add(macro);
        }

        GatherBuddy.Log.Debug($"[UserMacroLibrary] Added macro '{macro.Name}' (Recipe: {recipeId})");
        SaveToConfig();
    }

    public void RemoveMacro(string macroId)
    {
        var macro = _macros.Find(m => m.Id == macroId);
        if (macro != null)
        {
            _macros.Remove(macro);
            
            foreach (var list in _macrosByRecipe.Values)
                list.RemoveAll(m => m.Id == macroId);
            
            GatherBuddy.Log.Debug($"[UserMacroLibrary] Removed macro '{macro.Name}'");
            SaveToConfig();
        }
    }

    public List<UserMacro> GetMacrosForRecipe(uint recipeId)
    {
        if (_macrosByRecipe.TryGetValue(recipeId, out var macros))
            return macros;
        
        return new();
    }

    public UserMacro? GetMacroById(int id)
    {
        return _macros.Find(m => m.Id.GetHashCode() == id);
    }
    
    public UserMacro? GetMacroByStringId(string id)
    {
        return _macros.Find(m => m.Id == id);
    }

    public List<UserMacro> GetAllMacros()
    {
        return new(_macros);
    }

    public void Save() => SaveToConfig();

    public void Clear()
    {
        _macros.Clear();
        _macrosByRecipe.Clear();
        SaveToConfig();
    }

    public void LoadFromConfig()
    {
        try
        {
            var json = GatherBuddy.Config.UserMacros;
            if (string.IsNullOrWhiteSpace(json))
                return;

            var data = JsonSerializer.Deserialize<UserMacroLibraryData>(json);
            if (data == null)
                return;

            _macros.Clear();
            _macrosByRecipe.Clear();

            foreach (var macro in data.Macros)
            {
                _macros.Add(macro);
            }

            foreach (var kvp in data.MacrosByRecipe)
            {
                _macrosByRecipe[kvp.Key] = kvp.Value;
            }

            GatherBuddy.Log.Information($"[UserMacroLibrary] Loaded {_macros.Count} macros from config");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[UserMacroLibrary] Failed to load macros: {ex.Message}");
        }
    }

    private void SaveToConfig()
    {
        try
        {
            var data = new UserMacroLibraryData
            {
                Macros = _macros,
                MacrosByRecipe = _macrosByRecipe
            };

            var json = JsonSerializer.Serialize(data);
            GatherBuddy.Config.UserMacros = json;
            GatherBuddy.Config.Save();
            GatherBuddy.Log.Debug($"[UserMacroLibrary] Saved {_macros.Count} macros to config");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[UserMacroLibrary] Failed to save macros: {ex.Message}");
        }
    }
}

public class UserMacroLibraryData
{
    public List<UserMacro> Macros { get; set; } = new();
    public Dictionary<uint, List<UserMacro>> MacrosByRecipe { get; set; } = new();
}
