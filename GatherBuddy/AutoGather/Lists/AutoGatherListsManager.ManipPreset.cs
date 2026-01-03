using System;
using System.Linq;
using GatherBuddy.Classes;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using ElliLib.Filesystem;

namespace GatherBuddy.AutoGather.Lists;

public partial class AutoGatherListsManager
{
    public void AddList(AutoGatherList list, FileSystem<AutoGatherList>.Folder? folder = null)
    {
        folder ??= _fileSystem.Root;
        try
        {
            _fileSystem.CreateLeaf(folder, list.Name, list);
        }
        catch
        {
            _fileSystem.CreateDuplicateLeaf(folder, list.Name, list);
        }
        Save();
        if (list.HasItems())
            SetActiveItems();
    }

    public void DeleteList(AutoGatherList list)
    {
        if (!_fileSystem.TryGetValue(list, out var leaf))
            return;

        var enabled = list.HasItems();
        _fileSystem.Delete(leaf);
        Save();
        if (enabled)
            SetActiveItems();
    }

    public void MoveList(AutoGatherList list, FileSystem<AutoGatherList>.Folder targetFolder)
    {
        if (!_fileSystem.TryGetValue(list, out var leaf))
            return;

        try
        {
            _fileSystem.Move(leaf, targetFolder);
            Save();
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"Failed to move list: {e.Message}");
        }
    }

    public void CreateFolder(string name, FileSystem<AutoGatherList>.Folder? parent = null)
    {
        parent ??= _fileSystem.Root;
        try
        {
            _fileSystem.CreateFolder(parent, name);
            Save();
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"Failed to create folder: {e.Message}");
        }
    }

    public void DeleteFolder(FileSystem<AutoGatherList>.Folder folder)
    {
        if (folder.IsRoot)
            return;

        try
        {
            _fileSystem.Delete(folder);
            Save();
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"Failed to delete folder: {e.Message}");
        }
    }

    public void ChangeName(AutoGatherList list, string newName)
    {
        if (newName == list.Name || !_fileSystem.TryGetValue(list, out var leaf))
            return;

        try
        {
            _fileSystem.Rename(leaf, newName);
            list.Name = newName;
            Save();
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Warning($"Failed to rename list: {e.Message}");
        }
    }

    public void ChangeDescription(AutoGatherList list, string newDescription)
    {
        if (newDescription == list.Description)
            return;

        list.Description = newDescription;
        Save();
    }

    public void ToggleList(AutoGatherList list)
    {
        if (!list.Enabled && !ValidateFishingBait(list))
        {
            return;
        }
        
        if (!list.Enabled && !ValidateGatherablePerception(list))
        {
            return;
        }
        
        list.Enabled = !list.Enabled;
        Save();
        if (list.Items.Count > 0)
            SetActiveItems();
    }
    
    private unsafe bool ValidateFishingBait(AutoGatherList list)
    {
        try
        {
            var fishInList = list.Items.OfType<Fish>().Where(f => !f.IsSpearFish && list.EnabledItems.TryGetValue(f, out var enabled) && enabled).ToList();
            if (fishInList.Count == 0)
                return true;
            
            static uint GetInventoryItemCount(uint itemRowId)
            {
                return (uint)FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance()->GetInventoryItemCount(itemRowId < 100000 ? itemRowId : itemRowId - 100000, itemRowId >= 100000);
            }
            
            var missingBaits = new System.Collections.Generic.HashSet<uint>();
            const uint VersatileLureId = 29717;
            
            foreach (var fish in fishInList)
            {
                var baitId = fish.InitialBait?.Id ?? 0;
                if (baitId == 0)
                    continue;
                
                if (GatherBuddy.Config.AutoGatherConfig.UseExistingAutoHookPresets)
                {
                    var customBaitId = GetCustomPresetBaitId(fish.ItemId);
                    if (customBaitId.HasValue)
                    {
                        baitId = customBaitId.Value;
                        GatherBuddy.Log.Debug($"[Auto-Gather] Using custom preset bait ID {baitId} for fish {fish.ItemId}");
                    }
                }
                
                if (GetInventoryItemCount(baitId) == 0 && GetInventoryItemCount(VersatileLureId) == 0)
                {
                    missingBaits.Add(baitId);
                }
            }
            
            if (missingBaits.Count > 0)
            {
                var baitIds = string.Join(", ", missingBaits);
                Communicator.PrintError($"[Auto-Gather] Cannot enable list '{list.Name}': Missing required bait IDs (and no Versatile Lure): {baitIds}");
                GatherBuddy.Log.Error($"[Auto-Gather] List '{list.Name}' not enabled: Missing bait IDs {baitIds} and no Versatile Lure");
                return false;
            }
            
            return true;
        }
        catch (System.Exception ex)
        {
            GatherBuddy.Log.Error($"[Auto-Gather] Error validating fishing bait: {ex.Message}\n{ex.StackTrace}");
            return true;
        }
    }
    
    private unsafe bool ValidateSingleFishBait(IGatherable item)
    {
        if (item is not Fish fish || fish.IsSpearFish)
            return true;
        
        try
        {
            static uint GetInventoryItemCount(uint itemRowId)
            {
                return (uint)FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance()->GetInventoryItemCount(itemRowId < 100000 ? itemRowId : itemRowId - 100000, itemRowId >= 100000);
            }
            
            const uint VersatileLureId = 29717;
            var baitId = fish.InitialBait?.Id ?? 0;
            
            if (baitId == 0)
                return true;
            
            if (GatherBuddy.Config.AutoGatherConfig.UseExistingAutoHookPresets)
            {
                var customBaitId = GetCustomPresetBaitId(fish.ItemId);
                if (customBaitId.HasValue)
                    baitId = customBaitId.Value;
            }
            
            if (GetInventoryItemCount(baitId) == 0 && GetInventoryItemCount(VersatileLureId) == 0)
            {
                Communicator.PrintError($"[Auto-Gather] Cannot add/enable {fish.Name[GatherBuddy.Language]}: Missing bait ID {baitId} and no Versatile Lure");
                GatherBuddy.Log.Error($"[Auto-Gather] Fish {fish.ItemId} not enabled: Missing bait ID {baitId} and no Versatile Lure");
                return false;
            }
            
            return true;
        }
        catch (System.Exception ex)
        {
            GatherBuddy.Log.Error($"[Auto-Gather] Error validating single fish bait: {ex.Message}");
            return true;
        }
    }
    
    private bool ValidateGatherablePerception(AutoGatherList list)
    {
        try
        {
            var gatherablesInList = list.Items.OfType<Gatherable>().Where(g => list.EnabledItems.TryGetValue(g, out var enabled) && enabled).ToList();
            if (gatherablesInList.Count == 0)
                return true;
            
            var playerPerception = DiscipleOfLand.Perception;
            var insufficientPerception = new System.Collections.Generic.List<(string Name, int Required, int Current)>();
            
            foreach (var gatherable in gatherablesInList)
            {
                var requiredPerception = (int)gatherable.GatheringData.PerceptionReq;
                if (requiredPerception == 0)
                    continue;
                
                if (playerPerception < requiredPerception)
                {
                    insufficientPerception.Add((gatherable.Name[GatherBuddy.Language], requiredPerception, playerPerception));
                }
            }
            
            if (insufficientPerception.Count > 0)
            {
                var itemDetails = string.Join(", ", insufficientPerception.Select(x => $"{x.Name} (needs {x.Required})"));
                Communicator.PrintError($"[Auto-Gather] Cannot enable list '{list.Name}': Insufficient perception (current: {playerPerception}): {itemDetails}");
                GatherBuddy.Log.Error($"[Auto-Gather] List '{list.Name}' not enabled: Insufficient perception {playerPerception}");
                return false;
            }
            
            return true;
        }
        catch (System.Exception ex)
        {
            GatherBuddy.Log.Error($"[Auto-Gather] Error validating gatherable perception: {ex.Message}\n{ex.StackTrace}");
            return true;
        }
    }
    
    private bool ValidateSingleGatherablePerception(IGatherable item)
    {
        if (item is not Gatherable gatherable)
            return true;
        
        try
        {
            var requiredPerception = (int)gatherable.GatheringData.PerceptionReq;
            if (requiredPerception == 0)
                return true;
            
            var playerPerception = DiscipleOfLand.Perception;
            
            if (playerPerception < requiredPerception)
            {
                Communicator.PrintError($"[Auto-Gather] Cannot add/enable {gatherable.Name[GatherBuddy.Language]}: Insufficient perception (needs {requiredPerception}, current: {playerPerception})");
                GatherBuddy.Log.Error($"[Auto-Gather] Gatherable {gatherable.ItemId} not enabled: Needs {requiredPerception} perception, current: {playerPerception}");
                return false;
            }
            
            return true;
        }
        catch (System.Exception ex)
        {
            GatherBuddy.Log.Error($"[Auto-Gather] Error validating single gatherable perception: {ex.Message}");
            return true;
        }
    }
    
    private uint? GetCustomPresetBaitId(uint fishId)
    {
        try
        {
            var pluginConfigsDir = Dalamud.PluginInterface.ConfigDirectory.Parent?.FullName;
            if (string.IsNullOrEmpty(pluginConfigsDir))
                return null;

            var configPath = System.IO.Path.Combine(pluginConfigsDir, "AutoHook.json");
            if (!System.IO.File.Exists(configPath))
                return null;

            var json = System.IO.File.ReadAllText(configPath);
            var config = Newtonsoft.Json.Linq.JObject.Parse(json);

            var customPresets = config["HookPresets"]?["CustomPresets"] as Newtonsoft.Json.Linq.JArray;
            if (customPresets == null)
                return null;

            foreach (var preset in customPresets)
            {
                var presetName = preset?["PresetName"]?.ToString();
                if (presetName != null && presetName.Equals(fishId.ToString(), System.StringComparison.Ordinal))
                {
                    var forcedBaitIdToken = preset?["ExtraCfg"]?["ForcedBaitId"];
                    if (forcedBaitIdToken != null)
                    {
                        var forcedBaitId = forcedBaitIdToken.ToObject<uint>();
                        if (forcedBaitId > 0)
                        {
                            GatherBuddy.Log.Debug($"[Auto-Gather] Found custom preset for fish {fishId} with bait ID {forcedBaitId}");
                            return forcedBaitId;
                        }
                    }
                }
            }

            return null;
        }
        catch (System.Exception ex)
        {
            GatherBuddy.Log.Error($"[Auto-Gather] Error reading AutoHook config for bait validation: {ex.Message}");
            return null;
        }
    }

    public void SetFallback(AutoGatherList list, bool value)
    {
        list.Fallback = value;
        Save();
        if (list.Items.Count > 0)
            SetActiveItems();
    }

    public void AddItem(AutoGatherList list, IGatherable item)
    {
        if (list.Add(item))
        {
            if (list.Enabled && !ValidateSingleFishBait(item))
            {
                list.SetEnabled(item, false);
            }
            if (list.Enabled && !ValidateSingleGatherablePerception(item))
            {
                list.SetEnabled(item, false);
            }
            Save();
            if (list.Enabled)
                SetActiveItems();
        }
    }

    public void RemoveItem(AutoGatherList list, int idx)
    {
        if (idx < 0 || idx >= list.Items.Count)
            return;

        list.RemoveAt(idx);
        Save();
        if (list.Enabled)
            SetActiveItems();
    }

    public void ChangeItem(AutoGatherList list, IGatherable item, int idx)
    {
        if (idx < 0 || idx >= list.Items.Count)
            return;

        if (list.Replace(idx, item))
        {
            Save();
            if (list.Enabled)
                SetActiveItems();
        }
    }

    public void ChangeQuantity(AutoGatherList list, IGatherable item, uint quantity)
    {
        if (list.SetQuantity(item, quantity))
        {
            Save();
            if (list.Enabled)
                SetActiveItems();
        }
    }

    public void ChangeEnabled(AutoGatherList list, IGatherable item, bool enabled)
    {
        if (enabled && list.Enabled && !ValidateSingleFishBait(item))
        {
            return;
        }
        
        if (enabled && list.Enabled && !ValidateSingleGatherablePerception(item))
        {
            return;
        }
        
        if (list.SetEnabled(item, enabled))
        {
            Save();
            if (list.Enabled)
                SetActiveItems();
        }
    }

    public void MoveItem(AutoGatherList list, int idx1, int idx2)
    {
        if (list.Move(idx1, idx2))
        {
            Save();
            if (list.Enabled)
                SetActiveItems();
        }
    }

    public void ChangePreferredLocation(AutoGatherList list, IGatherable? item, ILocation? location)
    {
        if (item == null)
            return;
        if (list.SetPreferredLocation(item, location))
        {
            Save();
        }
    }

    public event Action? ListOrderChanged;

    public void MoveListUp(AutoGatherList list)
    {
        if (!_fileSystem.TryGetValue(list, out var leaf))
            return;

        var parent = leaf.Parent;
        var siblings = parent.GetLeaves().OrderBy(l => l.Value.Order).ToList();
        var index = siblings.IndexOf(leaf);
        
        if (index > 0)
        {
            var prevList = siblings[index - 1].Value;
            var temp = prevList.Order;
            prevList.Order = list.Order;
            list.Order = temp;
            Save();
            ListOrderChanged?.Invoke();
        }
    }

    public void MoveListDown(AutoGatherList list)
    {
        if (!_fileSystem.TryGetValue(list, out var leaf))
            return;

        var parent = leaf.Parent;
        var siblings = parent.GetLeaves().OrderBy(l => l.Value.Order).ToList();
        var index = siblings.IndexOf(leaf);
        
        if (index < siblings.Count - 1)
        {
            var nextList = siblings[index + 1].Value;
            var temp = nextList.Order;
            nextList.Order = list.Order;
            list.Order = temp;
            Save();
            ListOrderChanged?.Invoke();
        }
    }

    public ILocation? GetPreferredLocation(IGatherable item)
    {
        foreach (var list in _fileSystem.Select(kvp => kvp.Key))
        {
            if (list.Enabled && !list.Fallback && list.PreferredLocations.TryGetValue(item, out var loc))
            {
                return loc;
            }
        }
        return null;
    }
}
