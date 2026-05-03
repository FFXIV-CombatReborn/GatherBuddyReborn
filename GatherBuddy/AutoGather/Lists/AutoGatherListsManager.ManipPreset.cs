using ElliLib.Filesystem;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.Interfaces;
using GatherBuddy.Plugin;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

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
            var fishInList = list.Entries.Where(e => e.Enabled).Select(e => e.Item).OfType<Fish>().Where(f => !f.IsSpearFish).ToList();
            if (fishInList.Count == 0)
                return true;

            static uint GetInventoryItemCount(uint itemRowId)
            {
                return (uint)FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance()->GetInventoryItemCount(itemRowId < 100000 ? itemRowId : itemRowId - 100000, itemRowId >= 100000);
            }

            var missingBaits = new System.Collections.Generic.HashSet<uint>();
            const uint VersatileLureId = 29717;
            var versatileLureCount = GetInventoryItemCount(VersatileLureId);
            var customPresetBaitIds = GatherBuddy.Config.AutoGatherConfig.UseExistingAutoHookPresets
                ? GetCustomPresetBaitIds()
                : null;
            var inventoryCounts = new Dictionary<uint, uint>();
            var stopwatch = Stopwatch.StartNew();
            foreach (var fish in fishInList)
            {
                var baitId = fish.InitialBait?.Id ?? 0;
                if (baitId == 0)
                    continue;

                if (customPresetBaitIds != null && customPresetBaitIds.TryGetValue(fish.ItemId, out var customBaitId))
                {
                    baitId = customBaitId;
                    GatherBuddy.Log.Debug($"[Auto-Gather] Using custom preset bait ID {baitId} for fish {fish.ItemId}");
                }

                if (!inventoryCounts.TryGetValue(baitId, out var baitCount))
                {
                    baitCount = GetInventoryItemCount(baitId);
                    inventoryCounts[baitId] = baitCount;
                }

                if (baitCount > 0 || versatileLureCount > 0)
                    continue;

                missingBaits.Add(baitId);
            }

            stopwatch.Stop();
            if (stopwatch.ElapsedMilliseconds >= 10)
                GatherBuddy.Log.Debug($"[Auto-Gather] Validated fishing bait for list '{list.Name}' in {stopwatch.ElapsedMilliseconds} ms ({fishInList.Count} fish, {inventoryCounts.Count} unique bait IDs).");
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
            var gatherablesInList = list.Entries.Where(e => e.Enabled).Select(e => e.Item).OfType<Gatherable>().ToList();
            if (gatherablesInList.Count == 0)
                return true;
            
            var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
            var isMiner = currentJob == 16;
            var isBotanist = currentJob == 17;
            
            if (!isMiner && !isBotanist)
            {
                GatherBuddy.Log.Debug($"[Auto-Gather] Skipping perception validation - player not on Miner or Botanist (current job: {currentJob})");
                return true;
            }
            
            var playerPerception = DiscipleOfLand.Perception;
            var insufficientPerception = new System.Collections.Generic.List<(string Name, int Required, int Current)>();
            
            foreach (var gatherable in gatherablesInList)
            {
                var requiredPerception = (int)gatherable.GatheringData.PerceptionReq;
                if (requiredPerception == 0)
                    continue;
                
                var gatheringType = gatherable.GatheringType.ToGroup();
                if ((isMiner && gatheringType != GatheringType.Miner) || (isBotanist && gatheringType != GatheringType.Botanist))
                {
                    continue;
                }
                
                GatherBuddy.Log.Debug($"[Auto-Gather] Validating {gatherable.Name[GatherBuddy.Language]}: requires {requiredPerception} perception (current: {playerPerception})");
                
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
            
            var currentJob = Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
            var gatheringType = gatherable.GatheringType.ToGroup();
            var isMiner = currentJob == 16;
            var isBotanist = currentJob == 17;
            
            if (!isMiner && !isBotanist)
            {
                GatherBuddy.Log.Debug($"[Auto-Gather] Skipping perception validation for {gatherable.Name[GatherBuddy.Language]} - player not on Miner or Botanist (current job: {currentJob})");
                return true;
            }
            
            if ((isMiner && gatheringType != GatheringType.Miner) || (isBotanist && gatheringType != GatheringType.Botanist))
            {
                GatherBuddy.Log.Debug($"[Auto-Gather] Skipping perception validation for {gatherable.Name[GatherBuddy.Language]} - item is for different gathering job");
                return true;
            }
            
            var playerPerception = DiscipleOfLand.Perception;
            
            GatherBuddy.Log.Debug($"[Auto-Gather] Validating {gatherable.Name[GatherBuddy.Language]}: requires {requiredPerception} perception (current: {playerPerception})");
            
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
    
    private Dictionary<uint, uint>? GetCustomPresetBaitIds()
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

            var customPresetBaitIds = new Dictionary<uint, uint>();
            foreach (var preset in customPresets)
            {
                var presetName = preset?["PresetName"]?.ToString();
                if (presetName == null || !uint.TryParse(presetName, out var presetFishId))
                    continue;

                var forcedBaitIdToken = preset?["ExtraCfg"]?["ForcedBaitId"];
                if (forcedBaitIdToken == null)
                    continue;

                var forcedBaitId = forcedBaitIdToken.ToObject<uint>();
                if (forcedBaitId > 0)
                    customPresetBaitIds[presetFishId] = forcedBaitId;
            }

            return customPresetBaitIds.Count > 0 ? customPresetBaitIds : null;
        }
        catch (System.Exception ex)
        {
            GatherBuddy.Log.Error($"[Auto-Gather] Error reading AutoHook config for bait validation: {ex.Message}");
            return null;
        }
    }

    private uint? GetCustomPresetBaitId(uint fishId)
    {
        var customPresetBaitIds = GetCustomPresetBaitIds();
        if (customPresetBaitIds == null || !customPresetBaitIds.TryGetValue(fishId, out var forcedBaitId))
            return null;

        GatherBuddy.Log.Debug($"[Auto-Gather] Found custom preset for fish {fishId} with bait ID {forcedBaitId}");
        return forcedBaitId;
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
        if (AddItemInternal(list, item, null))
        {
            Save();
            if (list.Enabled)
                SetActiveItems();
        }
    }

    public void RemoveItem(AutoGatherList list, int idx)
    {
        if (idx < 0 || idx >= list.Entries.Count)
            return;

        list.RemoveAt(idx);
        Save();
        if (list.Enabled)
            SetActiveItems();
    }

    public void ChangeItem(AutoGatherList list, IGatherable item, int idx)
    {
        if (idx < 0 || idx >= list.Entries.Count)
            return;

        if (list.Replace(idx, item))
        {
            Save();
            if (list.Enabled)
                SetActiveItems();
        }
    }

    public void ChangeQuantity(AutoGatherList list, int idx, uint quantity)
    {
        if (list.SetQuantity(idx, quantity))
        {
            Save();
            if (list.Enabled)
                SetActiveItems();
        }
    }

    public void ChangeEnabled(AutoGatherList list, int idx, bool enabled)
    {
        if (idx < 0 || idx >= list.Entries.Count)
            return;

        var item = list.Entries[idx].Item;
        if (enabled && list.Enabled && !ValidateSingleFishBait(item))
        {
            return;
        }
        
        if (enabled && list.Enabled && !ValidateSingleGatherablePerception(item))
        {
            return;
        }
        
        if (list.SetEnabled(idx, enabled))
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

    public void MoveItem(AutoGatherList source, AutoGatherList destination, int idx)
    {
        var entry = source.Entries[idx];
        if (destination.Add(entry.Item, entry.Quantity, entry.PreferredLocation))
        {
            destination.SetEnabled(destination.Entries.Count - 1, entry.Enabled);
        }
        else
        {
            var existingIdx = destination.Entries
                .Select((e, i) => (Entry: e, Index: i))
                .First(x => x.Entry.Item == entry.Item && x.Entry.PreferredLocation == entry.PreferredLocation)
                .Index;
            destination.SetQuantity(existingIdx, destination.Entries[existingIdx].Quantity + entry.Quantity);
        }
        source.RemoveAt(idx);
        Save();
        if (source.Enabled || destination.Enabled)
            SetActiveItems();
    }

    public void ChangePreferredLocation(AutoGatherList list, int idx, ILocation? location)
    {
        if (list.SetPreferredLocation(idx, location))
        {
            Save();
            if (list.Enabled)
                SetActiveItems();
        }
    }

    public event Action? ListOrderChanged;

    public void MoveList(FileSystem<AutoGatherList>.Leaf movedList, FileSystem<AutoGatherList>.Leaf insertAt, bool movedFromUpperPart)
    {
        if (movedList.Parent != insertAt.Parent)
            throw new InvalidOperationException();

        var insertIdx = insertAt.Value.Order;
        if (movedFromUpperPart || movedList.Value.Order < insertIdx) insertIdx++;

        // Using an O(n) algorithm makes the numbering sparse, but it's fine for ordering.
        foreach (var leaf in movedList.Parent.GetLeaves())
        {
            if (leaf.Value.Order >= insertIdx)
                leaf.Value.Order++;
        }
        movedList.Value.Order = insertIdx;
        Save();
        ListOrderChanged?.Invoke();        
    }

    public void MoveListUp(FileSystem<AutoGatherList>.Leaf list)
        => MoveListOnce(list, false);

    public void MoveListDown(FileSystem<AutoGatherList>.Leaf list)
        => MoveListOnce(list, true);

    private void MoveListOnce(FileSystem<AutoGatherList>.Leaf list, bool down)
    {
        FileSystem<AutoGatherList>.Leaf? neighbor = null;
        // XOR flips the comparison, finding the right neighbor instead of the left when true.
        foreach (var sibling in list.Parent.GetLeaves())
        {
            if (sibling != list && sibling.Value.Order < list.Value.Order ^ down && (neighbor == null || neighbor.Value.Order < sibling.Value.Order ^ down))
                neighbor = sibling;
        }

        if (neighbor != null)
        {
            (list.Value.Order, neighbor.Value.Order) = (neighbor.Value.Order, list.Value.Order);
            Save();
            ListOrderChanged?.Invoke();
        }
    }

    private bool AddItemInternal(AutoGatherList list, IGatherable item, ILocation? location)
    {
        if (!list.Add(item, preferredLocation: location))
            return false;

        var idx = list.Entries.Count - 1;
        if (list.Enabled && !ValidateSingleFishBait(item))
        {
            list.SetEnabled(idx, false);
        }

        if (list.Enabled && !ValidateSingleGatherablePerception(item))
        {
            list.SetEnabled(idx, false);
        }

        return true;
    }
}
