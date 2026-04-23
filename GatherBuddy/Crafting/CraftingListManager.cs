using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace GatherBuddy.Crafting;

public class CraftingListManager
{
    private List<CraftingListDefinition> _lists = new();
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CraftingListDefinition> Lists => _lists.AsReadOnly();
    public bool HasFolders => GetKnownFolderPaths().Count != 0;

    public CraftingListManager()
    {
        Load();
    }

    public bool IsNameUnique(string name, int? excludeId = null)
    {
        return !_lists.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (!excludeId.HasValue || x.ID != excludeId.Value));
    }
    public CraftingListDefinition CreateNewList(string name, bool ephemeral = false, string? folderPath = null)
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
        var normalizedFolderPath = NormalizeFolderPath(folderPath);
        if (!string.IsNullOrEmpty(normalizedFolderPath))
            EnsureFolderPath(normalizedFolderPath);

        var rng = new Random();
        var proposedId = rng.Next(100, 50000);
        while (_lists.Any(x => x.ID == proposedId))
        {
            proposedId = rng.Next(100, 50000);
        }

        var list = new CraftingListDefinition
        {
            ID = proposedId,
            Name = name,
            FolderPath = normalizedFolderPath,
            Ephemeral = ephemeral
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
            existing.FolderPath  = NormalizeFolderPath(list.FolderPath);
            existing.Recipes     = list.Recipes;
            existing.SkipIfEnough = list.SkipIfEnough;
            existing.Materia = list.Materia;
            existing.Repair = list.Repair;
            existing.RepairPercent = list.RepairPercent;
            existing.Consumables = list.Consumables;
            existing.Ephemeral = list.Ephemeral;
            Save();
            return true;
        }
        return false;
    }

    public IReadOnlyList<CraftingListDefinition> GetListsInFolder(string? folderPath = null)
    {
        var normalizedFolderPath = NormalizeFolderPath(folderPath);
        return _lists
            .Where(list => list.FolderPath.Equals(normalizedFolderPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public IReadOnlyList<string> GetDirectSubfolderPaths(string? parentFolderPath = null)
    {
        var normalizedParent = NormalizeFolderPath(parentFolderPath);
        var prefix = string.IsNullOrEmpty(normalizedParent)
            ? string.Empty
            : normalizedParent + "/";

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folderPath in GetKnownFolderPaths())
        {
            if (!folderPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var remainder = folderPath[prefix.Length..];
            if (remainder.Length == 0)
                continue;

            var separator = remainder.IndexOf('/');
            folders.Add(separator >= 0
                ? prefix + remainder[..separator]
                : folderPath);
        }

        return folders
            .OrderBy(GetFolderDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetAllFolderPaths()
        => GetKnownFolderPaths()
            .OrderBy(FormatFolderPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool IsFolderNameAvailable(string name, string? parentFolderPath = null)
    {
        var trimmedName = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
            return false;
        if (trimmedName.Contains('/') || trimmedName.Contains('\\'))
            return false;

        var normalizedParent = NormalizeFolderPath(parentFolderPath);
        var newFolderPath = string.IsNullOrEmpty(normalizedParent)
            ? trimmedName
            : $"{normalizedParent}/{trimmedName}";

        return !GetDirectSubfolderPaths(normalizedParent).Any(folderPath => folderPath.Equals(newFolderPath, StringComparison.OrdinalIgnoreCase))
            && !GetListsInFolder(normalizedParent).Any(list => list.Name.Equals(trimmedName, StringComparison.OrdinalIgnoreCase));
    }

    public bool CreateFolder(string name, string? parentFolderPath = null)
    {
        var trimmedName = name.Trim();
        if (!IsFolderNameAvailable(trimmedName, parentFolderPath))
        {
            GatherBuddy.Log.Debug($"[CraftingListManager] Failed to create folder '{name}' under '{NormalizeFolderPath(parentFolderPath)}'");
            return false;
        }

        var normalizedParent = NormalizeFolderPath(parentFolderPath);
        var folderPath = string.IsNullOrEmpty(normalizedParent)
            ? trimmedName
            : $"{normalizedParent}/{trimmedName}";

        EnsureFolderPath(folderPath);
        GatherBuddy.Log.Information($"[CraftingListManager] Created folder '{folderPath}'");
        return true;
    }

    public bool CanDeleteFolder(string folderPath)
    {
        var normalizedFolderPath = NormalizeFolderPath(folderPath);
        return !string.IsNullOrEmpty(normalizedFolderPath)
            && !_lists.Any(list => IsInFolderTree(list.FolderPath, normalizedFolderPath));
    }

    public bool DeleteFolder(string folderPath)
    {
        var normalizedFolderPath = NormalizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(normalizedFolderPath))
            return false;
        if (!CanDeleteFolder(normalizedFolderPath))
        {
            GatherBuddy.Log.Debug($"[CraftingListManager] Refused to delete non-empty folder '{normalizedFolderPath}'");
            return false;
        }

        _folders.RemoveWhere(path => IsInFolderTree(path, normalizedFolderPath));
        GatherBuddy.Log.Information($"[CraftingListManager] Deleted folder '{normalizedFolderPath}'");
        return true;
    }

    public bool MoveListToFolder(CraftingListDefinition list, string? folderPath)
    {
        var existing = GetListByID(list.ID);
        if (existing == null)
        {
            GatherBuddy.Log.Debug($"[CraftingListManager] Failed to move list {list.ID} because it no longer exists");
            return false;
        }

        var normalizedFolderPath = NormalizeFolderPath(folderPath);
        if (!string.IsNullOrEmpty(normalizedFolderPath))
            EnsureFolderPath(normalizedFolderPath);

        if (existing.FolderPath.Equals(normalizedFolderPath, StringComparison.OrdinalIgnoreCase))
            return true;

        existing.FolderPath = normalizedFolderPath;
        GatherBuddy.Log.Debug($"[CraftingListManager] Moved list '{existing.Name}' to folder '{normalizedFolderPath}'");
        Save();
        return true;
    }

    public static string NormalizeFolderPath(string? folderPath)
        => string.IsNullOrWhiteSpace(folderPath)
            ? string.Empty
            : string.Join("/",
                folderPath
                    .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(part => part.Trim())
                    .Where(part => part.Length > 0));

    public static string GetFolderDisplayName(string folderPath)
    {
        var normalizedFolderPath = NormalizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(normalizedFolderPath))
            return string.Empty;

        var separator = normalizedFolderPath.LastIndexOf('/');
        return separator < 0
            ? normalizedFolderPath
            : normalizedFolderPath[(separator + 1)..];
    }

    public static string FormatFolderPath(string? folderPath)
        => string.IsNullOrEmpty(NormalizeFolderPath(folderPath))
            ? "Root"
            : NormalizeFolderPath(folderPath).Replace("/", " / ");

    private void Save()
    {
        try
        {
            var canonicalizedLists = 0;
            foreach (var list in _lists)
            {
                if (CanonicalizeOriginalItemQualitySettings(list))
                    canonicalizedLists++;
            }

            if (canonicalizedLists > 0)
                GatherBuddy.Log.Debug($"[CraftingListManager] Canonicalized original-item ingredient prefs in {canonicalizedLists} list(s) before save");
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
            _folders.Clear();
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
                var normalizedFolderPath = NormalizeFolderPath(_lists[i].FolderPath);
                if (_lists[i].FolderPath != normalizedFolderPath)
                {
                    _lists[i].FolderPath = normalizedFolderPath;
                    needsSave = true;
                }
                if (_lists[i].CreatedAt == default(DateTime))
                {
                    _lists[i].CreatedAt = baseTime.AddHours(i);
                    needsSave = true;
                }
                if (CanonicalizeOriginalItemQualitySettings(_lists[i]))
                    needsSave = true;
            }
            if (needsSave)
            {
                GatherBuddy.Log.Debug($"[CraftingListManager] Migrated {_lists.Count} lists with CreatedAt timestamps");
                Save();
            }
            
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

    private List<string> GetKnownFolderPaths()
    {
        var folders = new HashSet<string>(_folders, StringComparer.OrdinalIgnoreCase);
        foreach (var list in _lists)
        {
            AddFolderAndAncestors(folders, list.FolderPath);
        }

        return folders.ToList();
    }

    private void EnsureFolderPath(string folderPath)
    {
        AddFolderAndAncestors(_folders, folderPath);
    }

    private static void AddFolderAndAncestors(HashSet<string> folders, string? folderPath)
    {
        var normalizedFolderPath = NormalizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(normalizedFolderPath))
            return;

        var parts = normalizedFolderPath.Split('/');
        var current = string.Empty;
        foreach (var part in parts)
        {
            current = string.IsNullOrEmpty(current)
                ? part
                : $"{current}/{part}";
            folders.Add(current);
        }
    }

    private static bool IsInFolderTree(string? candidatePath, string folderPath)
    {
        var normalizedCandidatePath = NormalizeFolderPath(candidatePath);
        var normalizedFolderPath = NormalizeFolderPath(folderPath);
        return normalizedCandidatePath.Equals(normalizedFolderPath, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidatePath.StartsWith(normalizedFolderPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    public string? ExportList(int id)
    {
        var list = GetListByID(id);
        if (list == null) return null;

        var copy = JsonConvert.DeserializeObject<CraftingListDefinition>(JsonConvert.SerializeObject(list))!;
        CanonicalizeOriginalItemQualitySettings(copy);
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

    public (string? Url, string? Error) ExportListToTeamCraft(int id)
    {
        var list = GetListByID(id);
        if (list == null)
            return (null, "The selected list no longer exists.");
        if (list.Recipes.Count == 0)
            return (null, "The selected list contains no recipes.");

        const string baseUrl = "https://ffxivteamcraft.com/import/";
        var orderedItemIds = new List<uint>();
        var exportedItemQuantities = new Dictionary<uint, long>();

        foreach (var item in list.Recipes)
        {
            if (item.Quantity <= 0)
            {
                GatherBuddy.Log.Debug($"[CraftingListManager] Skipping TeamCraft export entry for recipe {item.RecipeId} in '{list.Name}' because its quantity is {item.Quantity}.");
                continue;
            }

            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe == null)
            {
                GatherBuddy.Log.Debug($"[CraftingListManager] Skipping TeamCraft export entry for recipe {item.RecipeId} in '{list.Name}' because the recipe could not be resolved.");
                continue;
            }

            var resultItemId = recipe.Value.ItemResult.RowId;
            if (resultItemId == 0 || recipe.Value.AmountResult == 0)
            {
                GatherBuddy.Log.Debug($"[CraftingListManager] Skipping TeamCraft export entry for recipe {item.RecipeId} in '{list.Name}' because the result item or amount is invalid.");
                continue;
            }

            var exportedQuantity = (long)item.Quantity * recipe.Value.AmountResult;
            if (!exportedItemQuantities.TryAdd(resultItemId, exportedQuantity))
                exportedItemQuantities[resultItemId] += exportedQuantity;
            else
                orderedItemIds.Add(resultItemId);
        }

        if (orderedItemIds.Count == 0)
            return (null, "The selected list has no TeamCraft-exportable recipes.");

        var payload = string.Join(";", orderedItemIds.Select(itemId => $"{itemId},null,{exportedItemQuantities[itemId]}"));
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        GatherBuddy.Log.Information($"[CraftingListManager] Exported list '{list.Name}' to TeamCraft with {orderedItemIds.Count} item(s)");
        return ($"{baseUrl}{base64}", null);
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
            newList.PrecraftOptions           = source.PrecraftOptions;
            newList.PrecraftCraftSettings     = source.PrecraftCraftSettings;
            newList.PrecraftRecipeOverrides   = source.PrecraftRecipeOverrides;
            newList.DefaultPrecraftSolverOverride = source.DefaultPrecraftSolverOverride;
            newList.DefaultFinalSolverOverride = source.DefaultFinalSolverOverride;
            newList.SkipIfEnough          = source.SkipIfEnough;
            newList.QuickSynthAll         = source.QuickSynthAll;
            newList.QuickSynthAllPreferNQ = source.QuickSynthAllPreferNQ;
            newList.QuickSynthAllPrecraftsOnly = source.QuickSynthAllPrecraftsOnly;
            CanonicalizeOriginalItemQualitySettings(newList);
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

    private static bool CanonicalizeOriginalItemQualitySettings(CraftingListDefinition list)
    {
        var changed = false;
        foreach (var item in list.Recipes)
        {
            if (item.CraftSettings != null && !item.CraftSettings.HasAnySettings())
            {
                item.CraftSettings = null;
                changed = true;
            }

            if (item.IngredientPreferences.Count == 0)
                continue;

            var hasCanonicalQualitySettings = item.CraftSettings?.UseAllNQ == true
                || item.CraftSettings?.IngredientPreferences.Count > 0;
            if (!hasCanonicalQualitySettings)
            {
                item.CraftSettings ??= new RecipeCraftSettings();
                item.CraftSettings.IngredientPreferences = new Dictionary<uint, int>(item.IngredientPreferences);
                changed = true;
            }

            item.IngredientPreferences.Clear();
            changed = true;

            if (item.CraftSettings != null && !item.CraftSettings.HasAnySettings())
            {
                item.CraftSettings = null;
                changed = true;
            }
        }

        return changed;
    }
}
