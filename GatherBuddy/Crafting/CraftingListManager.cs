using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace GatherBuddy.Crafting;

public class CraftingListManager
{
    private List<CraftingListDefinition> _lists = new();

    public IReadOnlyList<CraftingListDefinition> Lists => _lists.AsReadOnly();

    public CraftingListManager()
    {
        Load();
    }

    public bool IsNameUnique(string name, int? excludeId = null)
    {
        return !_lists.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (!excludeId.HasValue || x.ID != excludeId.Value));
    }

    public CraftingListDefinition CreateNewList(string name)
    {
        if (!IsNameUnique(name))
        {
            var suffix = 1;
            var originalName = name;
            while (!IsNameUnique(name))
            {
                name = $"{originalName} ({suffix})";
                suffix++;
            }
            GatherBuddy.Log.Information($"[CraftingListManager] List name '{originalName}' already exists, using '{name}' instead");
        }

        var rng = new Random();
        var proposedId = rng.Next(100, 50000);
        while (_lists.Any(x => x.ID == proposedId))
        {
            proposedId = rng.Next(100, 50000);
        }

        var list = new CraftingListDefinition
        {
            ID = proposedId,
            Name = name
        };
        
        _lists.Add(list);
        Save();
        return list;
    }

    public CraftingListDefinition? GetListByID(int id)
    {
        return _lists.FirstOrDefault(x => x.ID == id);
    }

    public CraftingListDefinition? GetListByName(string name)
    {
        return _lists.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public bool DeleteList(int id)
    {
        var list = GetListByID(id);
        if (list != null)
        {
            _lists.Remove(list);
            Save();
            return true;
        }
        return false;
    }

    public bool SaveList(CraftingListDefinition list)
    {
        var existing = GetListByID(list.ID);
        if (existing != null)
        {
            existing.Name        = list.Name;
            existing.Description = list.Description;
            existing.Recipes     = list.Recipes;
            existing.SkipIfEnough = list.SkipIfEnough;
            existing.Materia = list.Materia;
            existing.Repair = list.Repair;
            existing.RepairPercent = list.RepairPercent;
            existing.Consumables = list.Consumables;
            Save();
            return true;
        }
        return false;
    }

    private void Save()
    {
        try
        {
            GatherBuddy.Config.CraftingLists = JsonConvert.SerializeObject(_lists);
            GatherBuddy.Config.Save();
            GatherBuddy.Log.Debug($"[CraftingListManager] Saved {_lists.Count} crafting lists");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingListManager] Error saving lists: {ex.Message}");
        }
    }

    private void Load()
    {
        try
        {
            if (string.IsNullOrEmpty(GatherBuddy.Config.CraftingLists))
            {
                _lists = new();
                return;
            }

            _lists = JsonConvert.DeserializeObject<List<CraftingListDefinition>>(GatherBuddy.Config.CraftingLists) ?? new();
            
            var needsSave = false;
            var baseTime = DateTime.UtcNow.AddDays(-_lists.Count);
            for (int i = 0; i < _lists.Count; i++)
            {
                if (_lists[i].CreatedAt == default(DateTime))
                {
                    _lists[i].CreatedAt = baseTime.AddHours(i);
                    needsSave = true;
                }
            }
            
            if (needsSave)
            {
                GatherBuddy.Log.Debug($"[CraftingListManager] Migrated {_lists.Count} lists with CreatedAt timestamps");
                Save();
            }
            
            GatherBuddy.Log.Debug($"[CraftingListManager] Loaded {_lists.Count} crafting lists");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingListManager] Error loading lists: {ex.Message}");
            _lists = new();
        }
    }

    public void Reload()
    {
        Load();
    }

    public string? ExportList(int id)
    {
        var list = GetListByID(id);
        if (list == null) return null;

        var copy = JsonConvert.DeserializeObject<CraftingListDefinition>(JsonConvert.SerializeObject(list))!;
        copy.ID = 0;
        copy.CreatedAt = DateTime.MinValue;
        copy.ExpandedList.Clear();
        copy.DefaultPrecraftMacroId = null;
        copy.DefaultFinalMacroId = null;

        foreach (var item in copy.Recipes)
        {
            if (item.CraftSettings != null)
            {
                item.CraftSettings.SelectedMacroId = null;
                item.CraftSettings.MacroMode = MacroOverrideMode.Inherit;
            }
        }

        foreach (var settings in copy.PrecraftCraftSettings.Values)
        {
            settings.SelectedMacroId = null;
            settings.MacroMode = MacroOverrideMode.Inherit;
        }

        var json = JsonConvert.SerializeObject(copy, Formatting.None);
        GatherBuddy.Log.Information($"[CraftingListManager] Exported list '{list.Name}'");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public (CraftingListDefinition? List, string? Error) ImportList(string base64)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
            var source = JsonConvert.DeserializeObject<CraftingListDefinition>(json);
            if (source == null)
                return (null, "Failed to deserialize list data.");
            if (source.Recipes.Count == 0)
                return (null, "The exported list contains no recipes.");

            var name = string.IsNullOrWhiteSpace(source.Name) ? "Imported List" : source.Name;
            var newList = CreateNewList(name);
            newList.Recipes               = source.Recipes;
            newList.Consumables           = source.Consumables;
            newList.PrecraftOptions       = source.PrecraftOptions;
            newList.PrecraftCraftSettings = source.PrecraftCraftSettings;
            newList.SkipIfEnough          = source.SkipIfEnough;
            newList.QuickSynthAll         = source.QuickSynthAll;
            Save();

            GatherBuddy.Log.Information($"[CraftingListManager] Imported list '{newList.Name}' with {newList.Recipes.Count} recipes");
            return (newList, null);
        }
        catch (FormatException)
        {
            return (null, "Clipboard text is not valid base64. Make sure you copied the full export string.");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingListManager] Import failed: {ex.Message}");
            return (null, $"Import failed: {ex.Message}");
        }
    }
}
