using System;
using System.Collections.Generic;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Crafting;
using GatherBuddy.Interfaces;

namespace GatherBuddy.Plugin;

/// <summary>
/// IPC providers that let other plugins hand crafting-material work to GBR's auto-gather.
/// <para>
/// IPC is deliberately kept to switches over the public static <see cref="CraftingGatherBridge"/> entry points:
/// the IPC layer resolves nothing and stores nothing of its own — the calling plugin owns the list name it passes
/// and is responsible for remembering it. Internal list state (which list has which items) lives where it always
/// lives: <see cref="AutoGatherListsManager"/>, the same place every hand-edited list goes, through the same public
/// methods the crafting bridge already uses.
/// </para>
/// </summary>
public sealed class CraftingIpc
{
    private readonly GatherBuddy _plugin;

    public CraftingIpc(GatherBuddy plugin)
    {
        _plugin = plugin;
        EzIPC.Init(this, GatherBuddy.InternalName + ".Crafting");
    }

#pragma warning disable CA1822 // Mark members as static

    /// <summary>
    /// Create (or extend) a persistent auto-gather list from crafting materials.
    /// <paramref name="materials"/> maps item ids to the still-needed quantity; ids may be gatherables, fish,
    /// or approved Diadem inspection items. Quantities already covered by inventory are not gathered.
    /// When <paramref name="replace"/> is true, an existing list of the same name is deleted first so quantities
    /// never stack; when false, the call adds to the existing list.
    /// Returns the number of items in the list afterwards, or 0 if nothing in <paramref name="materials"/>
    /// is gatherable.
    /// </summary>
    [EzIPC]
    public int AddMaterialsToList(string listName, Dictionary<uint, int> materials, bool replace = false)
    {
        if (string.IsNullOrWhiteSpace(listName))
        {
            GatherBuddy.Log.Warning("[CraftingIpc] AddMaterialsToList refused: listName was null or empty.");
            return 0;
        }

        if (replace)
            DeleteList(listName);

        CraftingGatherBridge.CreatePersistentGatherList(listName, materials);
        return CountListItems(listName);
    }

    /// <summary>
    /// Delete the named auto-gather list, if it exists. Use this to clean up a list your plugin created.
    /// Refuses lists you did not create through IPC and always refuses GBR's fallback list.
    /// Returns true when a list was deleted.
    /// </summary>
    [EzIPC]
    public bool DeleteList(string listName)
    {
        if (string.IsNullOrWhiteSpace(listName))
        {
            GatherBuddy.Log.Warning("[CraftingIpc] DeleteList refused: listName was null or empty.");
            return false;
        }

        var list = FindList(listName);
        if (list == null)
            return false;

        if (list.Fallback)
        {
            GatherBuddy.Log.Warning(
                $"[CraftingIpc] DeleteList refused: '{listName}' is GBR's fallback list, not owned by an IPC caller.");
            return false;
        }

        _plugin.AutoGatherListsManager.DeleteList(list);
        GatherBuddy.Log.Information($"[CraftingIpc] Deleted gather list '{listName}'.");
        return true;
    }

    /// <summary>
    /// Number of (enabled) items currently in the named auto-gather list, or -1 when it does not exist.
    /// </summary>
    [EzIPC]
    public int GetListCount(string listName)
    {
        var list = FindList(listName);
        return list == null ? -1 : list.Items.Count;
    }

    /// <summary>
    /// Add a single item with a quantity to the named list, creating the list when missing.
    /// Adds a new entry, or raises the quantity of an existing entry by <paramref name="quantity"/>.
    /// Returns the number of items in the list afterwards; 0 when the item id is not a gatherable or fish.
    /// </summary>
    [EzIPC]
    public int AddItemToList(string listName, uint itemId, uint quantity)
    {
        if (string.IsNullOrWhiteSpace(listName))
        {
            GatherBuddy.Log.Warning("[CraftingIpc] AddItemToList refused: listName was null or empty.");
            return 0;
        }

        if (quantity == 0)
        {
            GatherBuddy.Log.Warning($"[CraftingIpc] AddItemToList refused: quantity for item {itemId} was 0.");
            return CountListItems(listName);
        }

        IGatherable? item =
            GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var gatherable) ? gatherable
            : GatherBuddy.GameData.Fishes.TryGetValue(itemId, out var fish)           ? fish
            : null;
        if (item == null)
        {
            GatherBuddy.Log.Debug($"[CraftingIpc] Item {itemId} is not a gatherable or fish; nothing added.");
            return CountListItems(listName);
        }

        var list = FindList(listName);
        if (list == null)
        {
            list = new AutoGatherList() { Name = listName, Enabled = false };
            list.Add(item, quantity);
            _plugin.AutoGatherListsManager.AddList(list);
            GatherBuddy.Log.Information($"[CraftingIpc] Created gather list '{listName}' with '{item.Name[GatherBuddy.Language]}' x{quantity}.");
            return list.Items.Count;
        }

        if (!list.Quantities.TryGetValue(item, out var existing))
        {
            list.Add(item, quantity);
            _plugin.AutoGatherListsManager.Save();
            _plugin.AutoGatherListsManager.SetActiveItems();
            GatherBuddy.Log.Information($"[CraftingIpc] Added '{item.Name[GatherBuddy.Language]}' x{quantity} to gather list '{listName}'.");
        }
        else
        {
            list.SetQuantity(item, existing + quantity);
            _plugin.AutoGatherListsManager.Save();
            _plugin.AutoGatherListsManager.SetActiveItems();
            GatherBuddy.Log.Information($"[CraftingIpc] Raised '{item.Name[GatherBuddy.Language]}' to x{existing + quantity} in gather list '{listName}'.");
        }

        return list.Items.Count;
    }

#pragma warning restore CA1822

    private AutoGatherList? FindList(string listName)
    {
        foreach (var list in _plugin.AutoGatherListsManager.Lists)
        {
            if (string.Equals(list.Name, listName, StringComparison.OrdinalIgnoreCase))
                return list;
        }

        return null;
    }

    private int CountListItems(string listName)
        => FindList(listName)?.Items.Count ?? 0;
}
