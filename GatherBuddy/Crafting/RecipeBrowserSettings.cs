using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace GatherBuddy.Crafting;

public enum MacroOverrideMode
{
    Inherit,
    Specific,
}

public enum SolverOverrideMode
{
    Default,
    StandardSolver,
    RaphaelSolver,
    ProgressOnlySolver,
}

public class RecipeCraftSettings
{
    public ConsumableOverrideMode FoodMode { get; set; } = ConsumableOverrideMode.Inherit;
    public uint? FoodItemId { get; set; }
    public bool FoodHQ { get; set; }
    public ConsumableOverrideMode MedicineMode { get; set; } = ConsumableOverrideMode.Inherit;
    public uint? MedicineItemId { get; set; }
    public bool MedicineHQ { get; set; }
    public ConsumableOverrideMode ManualMode { get; set; } = ConsumableOverrideMode.Inherit;
    public uint? ManualItemId { get; set; }
    public ConsumableOverrideMode SquadronManualMode { get; set; } = ConsumableOverrideMode.Inherit;
    public uint? SquadronManualItemId { get; set; }
    public Dictionary<uint, int> IngredientPreferences { get; set; } = new();
    public bool UseAllNQ { get; set; } = false;
    public string? SelectedMacroId { get; set; }
    public MacroOverrideMode MacroMode { get; set; } = MacroOverrideMode.Inherit;
    public SolverOverrideMode SolverOverride { get; set; } = SolverOverrideMode.Default;

    public bool HasAnySettings()
    {
        return FoodMode != ConsumableOverrideMode.Inherit
            || MedicineMode != ConsumableOverrideMode.Inherit
            || ManualMode != ConsumableOverrideMode.Inherit
            || SquadronManualMode != ConsumableOverrideMode.Inherit
            || FoodItemId.HasValue
            || MedicineItemId.HasValue
            || ManualItemId.HasValue
            || SquadronManualItemId.HasValue
            || IngredientPreferences.Count > 0
            || UseAllNQ
            || !string.IsNullOrEmpty(SelectedMacroId)
            || SolverOverride != SolverOverrideMode.Default;
    }

    public void Clear()
    {
        FoodMode = ConsumableOverrideMode.Inherit;
        FoodItemId = null;
        FoodHQ = false;
        MedicineMode = ConsumableOverrideMode.Inherit;
        MedicineItemId = null;
        MedicineHQ = false;
        ManualMode = ConsumableOverrideMode.Inherit;
        ManualItemId = null;
        SquadronManualMode = ConsumableOverrideMode.Inherit;
        SquadronManualItemId = null;
        IngredientPreferences.Clear();
        UseAllNQ = false;
        SelectedMacroId = null;
        MacroMode = MacroOverrideMode.Inherit;
        SolverOverride = SolverOverrideMode.Default;
    }
}

public class RecipeBrowserSettings
{
    private Dictionary<uint, RecipeCraftSettings> _settings = new();

    public RecipeCraftSettings GetOrCreate(uint recipeId)
    {
        if (!_settings.TryGetValue(recipeId, out var settings))
        {
            settings = new RecipeCraftSettings();
            _settings[recipeId] = settings;
        }
        return settings;
    }

    public RecipeCraftSettings? Get(uint recipeId)
    {
        return _settings.TryGetValue(recipeId, out var settings) ? settings : null;
    }

    public bool Has(uint recipeId)
    {
        return _settings.TryGetValue(recipeId, out var settings) && settings.HasAnySettings();
    }

    public void Set(uint recipeId, RecipeCraftSettings settings)
    {
        if (settings.HasAnySettings())
            _settings[recipeId] = settings;
        else
            _settings.Remove(recipeId);
    }

    public void Remove(uint recipeId)
    {
        _settings.Remove(recipeId);
    }

    public void Save()
    {
        try
        {
            GatherBuddy.Config.RecipeBrowserSettings = JsonConvert.SerializeObject(_settings);
            GatherBuddy.Config.Save();
            GatherBuddy.Log.Debug($"[RecipeBrowserSettings] Saved {_settings.Count} recipe settings");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[RecipeBrowserSettings] Error saving: {ex.Message}");
        }
    }

    public void Load()
    {
        try
        {
            if (string.IsNullOrEmpty(GatherBuddy.Config.RecipeBrowserSettings))
            {
                _settings = new();
                return;
            }

            _settings = JsonConvert.DeserializeObject<Dictionary<uint, RecipeCraftSettings>>(GatherBuddy.Config.RecipeBrowserSettings) ?? new();
            
            var toRemove = _settings.Where(kvp => !kvp.Value.HasAnySettings()).Select(kvp => kvp.Key).ToList();
            foreach (var key in toRemove)
                _settings.Remove(key);

            GatherBuddy.Log.Debug($"[RecipeBrowserSettings] Loaded {_settings.Count} recipe settings");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[RecipeBrowserSettings] Error loading: {ex.Message}");
            _settings = new();
        }
    }

    public void Clear()
    {
        _settings.Clear();
        Save();
    }
}
