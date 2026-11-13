using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.Classes;
using GatherBuddy.Interfaces;
using System;
using System.Collections.Generic;

namespace GatherBuddy.Plugin;

public class ContextMenu : IDisposable
{
    private readonly IContextMenu _contextMenu;
    private readonly Executor _executor;
    private IGatherable? _lastGatherable;
    private uint? _lastRecipeId;
    private uint? _lastVendorBuyListItemId;
    private readonly GatherBuddy _plugin;

    private readonly MenuItem _menuItem;
    private readonly MenuItem _menuItemAuto;
    private readonly MenuItem _menuItemCrafting;
    private readonly MenuItem _menuItemVulcanRecipe;
    private readonly MenuItem _menuItemVendorBuyList;

    public ContextMenu(GatherBuddy plugin, IContextMenu menu, Executor executor)
    {
        _plugin = plugin;
        _contextMenu = menu;
        _executor = executor;

        _menuItem = new MenuItem
        {
            IsEnabled = true,
            IsReturn = false,
            PrefixChar = 'G',
            Name = "Gather Manually",
            OnClicked = OnClick,
            IsSubmenu = false,
            PrefixColor = 42,
        };

        _menuItemAuto = new MenuItem
        {
            IsEnabled = true,
            IsReturn = false,
            PrefixChar = 'G',
            Name = "Add to Auto-Gather List",
            OnClicked = OnClickAuto,
            IsSubmenu = false,
            PrefixColor = 42,
        };

        _menuItemCrafting = new MenuItem
        {
            IsEnabled = true,
            IsReturn = false,
            PrefixChar = 'V',
            Name = "Add to Crafting List",
            OnClicked = OnClickCrafting,
            IsSubmenu = true,
            PrefixColor = 42,
        };

        _menuItemVulcanRecipe = new MenuItem
        {
            IsEnabled = true,
            IsReturn = false,
            PrefixChar = 'V',
            Name = "Open in Vulcan",
            OnClicked = OnClickVulcanRecipe,
            IsSubmenu = false,
            PrefixColor = 42,
        };

        _menuItemVendorBuyList = new MenuItem
        {
            IsEnabled = true,
            IsReturn = false,
            PrefixChar = 'V',
            Name = "Add to Vendor Buy List",
            OnClicked = OnClickVendorBuyList,
            IsSubmenu = true,
            PrefixColor = 42,
        };

        if (GatherBuddy.Config.AddIngameContextMenus)
        {
            Enable();
        }
    }

    private static void OpenCreateVendorBuyListPopup(uint itemId)
    {
        var vendorBuyListWindow = GatherBuddy.VendorBuyListWindow;
        if (vendorBuyListWindow == null)
        {
            GatherBuddy.Log.Warning($"[ContextMenu] Unable to open Create Vendor List popup for item {itemId}: vendor buy list window unavailable.");
            return;
        }

        GatherBuddy.Log.Debug($"[ContextMenu] Opening Create Vendor List popup for item {itemId}");
        if (!vendorBuyListWindow.OpenCreateListPopup(itemId))
        {
            GatherBuddy.Log.Debug($"[ContextMenu] Unable to create a new vendor buy list for item {itemId}.");
        }
    }

    private static void AddItemToVendorBuyList(uint itemId, Guid listId, string listName)
    {
        if (!GatherBuddy.VendorBuyListManager.TryIncrementTarget(listId, itemId, 1, selectList: true, openWindow: true, announce: true))
        {
            GatherBuddy.Log.Debug($"[ContextMenu] Unable to add item {itemId} to vendor buy list '{listName}'.");
        }
    }

    private void OnClick(IMenuItemClickedArgs args)
    {
        if (_lastGatherable != null)
        {
            _executor.GatherItem(_lastGatherable);
        }
    }

    private void OnClickAuto(IMenuItemClickedArgs args)
    {
        if (_lastGatherable is Gatherable gatherable)
        {
            var preset = _plugin.Interface.CurrentAutoGatherList;

            if (preset == null)
            {
                preset = new();
                _plugin.AutoGatherListsManager.AddList(preset);
            }

            _plugin.AutoGatherListsManager.AddItem(preset, gatherable);
        }
    }

    private void OnClickVulcanRecipe(IMenuItemClickedArgs args)
    {
        if (!_lastRecipeId.HasValue)
        {
            GatherBuddy.Log.Debug("[ContextMenu] Vulcan recipe context menu clicked without a cached recipe id.");
            return;
        }

        var recipe = Crafting.RecipeManager.GetRecipe(_lastRecipeId.Value);
        if (!recipe.HasValue)
        {
            GatherBuddy.Log.Debug($"[ContextMenu] Unable to resolve recipe {_lastRecipeId.Value} for Vulcan context menu.");
            return;
        }

        var vulcanWindow = GatherBuddy.VulcanWindow;
        if (vulcanWindow == null)
        {
            GatherBuddy.Log.Warning($"[ContextMenu] Vulcan window unavailable for recipe {_lastRecipeId.Value}.");
            return;
        }

        GatherBuddy.Log.Debug($"[ContextMenu] Opening Vulcan to recipe {recipe.Value.RowId} for item {recipe.Value.ItemResult.RowId}");
        vulcanWindow.OpenToRecipe(recipe.Value.RowId);
    }

    private void OnClickCrafting(IMenuItemClickedArgs args)
    {
        if (!_lastRecipeId.HasValue)
        {
            GatherBuddy.Log.Debug("[ContextMenu] Crafting context menu clicked without a cached recipe id.");
            return;
        }

        var recipe = Crafting.RecipeManager.GetRecipe(_lastRecipeId.Value);
        if (!recipe.HasValue)
        {
            GatherBuddy.Log.Debug($"[ContextMenu] Unable to resolve recipe {_lastRecipeId.Value} for crafting context menu.");
            return;
        }

        var allLists = GatherBuddy.CraftingListManager.Lists;
        var menuItems = new List<MenuItem>
        {
            new()
            {
                Name = "Create New List...",
                PrefixChar = 'C',
                PrefixColor = 42,
                OnClicked = _ => OpenCreateCraftingListPopup(recipe.Value.RowId),
            },
        };

        if (allLists.Count > 0)
        {
            var maxLists = Math.Max(1, GatherBuddy.Config.MaxRecentCraftingListsInContextMenu);
            GatherBuddy.Log.Debug($"[ContextMenu] Total lists: {allLists.Count}, Max to show: {maxLists}");

            var orderedLists = new List<(Crafting.CraftingListDefinition List, int Index)>(allLists.Count);
            for (var listIndex = 0; listIndex < allLists.Count; listIndex++)
            {
                orderedLists.Add((allLists[listIndex], listIndex));
            }

            orderedLists.Sort(static (lhs, rhs) =>
            {
                var order = rhs.List.CreatedAt.CompareTo(lhs.List.CreatedAt);
                return order != 0 ? order : lhs.Index.CompareTo(rhs.Index);
            });
            var recentLists = new List<Crafting.CraftingListDefinition>(Math.Min(maxLists, orderedLists.Count));
            for (var listIndex = 0; listIndex < orderedLists.Count && listIndex < maxLists; listIndex++)
            {
                recentLists.Add(orderedLists[listIndex].List);
            }

            GatherBuddy.Log.Debug($"[ContextMenu] Recent lists filtered: {recentLists.Count}");

            foreach (var list in recentLists)
            {
                var menuItem = new MenuItem
                {
                    Name = list.Name,
                    PrefixChar = 'C',
                    PrefixColor = 42,
                    OnClicked = clickedArgs => AddRecipeToList(recipe.Value, list)
                };
                menuItems.Add(menuItem);
            }

            if (allLists.Count > maxLists)
            {
                var moreItem = new MenuItem
                {
                    Name = $"({allLists.Count - maxLists} more lists...)",
                    IsEnabled = false
                };
                menuItems.Add(moreItem);
            }
        }

        if (menuItems.Count > 0)
        {
            args.OpenSubmenu(menuItems);
        }
    }

    private void OnClickVendorBuyList(IMenuItemClickedArgs args)
    {
        if (!_lastVendorBuyListItemId.HasValue)
        {
            GatherBuddy.Log.Debug("[ContextMenu] Vendor buy-list context menu clicked without a cached item id.");
            return;
        }
        var itemId = _lastVendorBuyListItemId.Value;
        var menuItems = new List<MenuItem>
        {
            new()
            {
                Name = "Create New List...",
                PrefixChar = 'V',
                PrefixColor = 42,
                OnClicked = _ => OpenCreateVendorBuyListPopup(itemId),
            },
        };

        foreach (var list in Vulcan.Vendors.VendorBuyListManager.GetListsByCreation(true))
        {
            var listId = list.Id;
            var listName = list.Name;
            menuItems.Add(new MenuItem
            {
                Name = listName,
                PrefixChar = 'V',
                PrefixColor = 42,
                OnClicked = _ => AddItemToVendorBuyList(itemId, listId, listName),
            });
        }

        args.OpenSubmenu(menuItems);
    }

    private static void OpenCreateCraftingListPopup(uint recipeId)
    {
        var vulcanWindow = GatherBuddy.VulcanWindow;
        if (vulcanWindow == null)
        {
            GatherBuddy.Log.Warning($"[ContextMenu] Unable to open Create List popup for recipe {recipeId}: Vulcan window unavailable.");
            return;
        }

        GatherBuddy.Log.Debug($"[ContextMenu] Opening Create List popup for recipe {recipeId}");
        vulcanWindow.OpenCreateListPopup(recipeId);
    }

    private static void AddRecipeToList(Lumina.Excel.Sheets.Recipe recipe, Crafting.CraftingListDefinition list)
    {
        var existingItem = list.Recipes.Find(x => x.RecipeId == recipe.RowId);
        if (existingItem != null)
        {
            existingItem.Quantity += 1;
            GatherBuddy.Log.Information($"Increased quantity of {recipe.ItemResult.Value.Name.ExtractText()} in list '{list.Name}' to {existingItem.Quantity}");
        }
        else
        {
            list.AddRecipe(recipe.RowId, 1);
            GatherBuddy.Log.Information($"Added {recipe.ItemResult.Value.Name.ExtractText()} to list '{list.Name}'");
        }

        GatherBuddy.CraftingListManager.SaveList(list);
        Crafting.RaphaelAssessmentService.QueueWarmupForAddedListRecipe(recipe.RowId, list);
        GatherBuddy.VulcanWindow?.RefreshOpenCraftingList(list.ID);
    }

    public void Enable()
        => _contextMenu.OnMenuOpened += OnContextMenuOpened;

    public void Disable()
        => _contextMenu.OnMenuOpened -= OnContextMenuOpened;

    public void Dispose()
        => Disable();

    private unsafe void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        _lastRecipeId = null;
        _lastVendorBuyListItemId = null;

        var contextItemId = GetContextItemId(args);
        _lastGatherable = contextItemId.HasValue ? ResolveGatherable(contextItemId.Value) : null;
        if (contextItemId.HasValue && SupportsRecipeActions(args))
        {
            _lastRecipeId = GetRecipeIdFromContext(args);
        }

        if (contextItemId.HasValue && Vulcan.Vendors.VendorBuyListManager.CanAddSupportedItem(contextItemId.Value))
        {
            _lastVendorBuyListItemId = contextItemId.Value;
        }

        if (_lastGatherable != null)
        {
            args.AddMenuItem(_menuItem);
        }

        if (_lastGatherable is Gatherable)
        {
            args.AddMenuItem(_menuItemAuto);
        }

        if (GatherBuddy.Config.VulcanContextMenuEntries && _lastRecipeId.HasValue)
        {
            _menuItemCrafting.IsEnabled = true;
            _menuItemVulcanRecipe.IsEnabled = true;
            args.AddMenuItem(_menuItemCrafting);
            args.AddMenuItem(_menuItemVulcanRecipe);
        }
        if (GatherBuddy.Config.VulcanContextMenuEntries && _lastVendorBuyListItemId.HasValue)
        {
            _menuItemVendorBuyList.IsEnabled = true;
            args.AddMenuItem(_menuItemVendorBuyList);
        }
    }

    private unsafe uint? GetRecipeIdFromContext(IMenuOpenedArgs args)
    {
        var itemId = GetContextItemId(args);
        if (!itemId.HasValue)
        {
            return null;
        }

        var recipe = Crafting.RecipeManager.GetRecipeForItem(itemId.Value);
        return recipe?.RowId;
    }

    private static bool SupportsRecipeActions(IMenuOpenedArgs args)
        => args.MenuType is ContextMenuType.Inventory
        || args.AddonName is "RecipeNote" or "RecipeTree" or "RecipeMaterialList" or "ItemSearch" or "ChatLog" or "ContentsInfoDetail";

    private unsafe uint? GetContextItemId(IMenuOpenedArgs args)
    {
        if (args.MenuType is ContextMenuType.Inventory)
        {
            var target = (MenuTargetInventory)args.Target;
            return target.TargetItem.HasValue ? NormalizeItemId(target.TargetItem.Value.ItemId) : null;
        }

        return args.AddonName switch
        {
            null => GetSatisfactionSupplyItemId(),
            "ContentsInfoDetail" => NormalizeItemId(AgentContentsTimer.Instance()->ContextMenuItemId),
            "RecipeNote" => NormalizeItemId(AgentRecipeNote.Instance()->ContextMenuResultItemId),
            "RecipeTree" => NormalizeItemId(AgentRecipeItemContext.Instance()->ResultItemId),
            "RecipeMaterialList" => NormalizeItemId(AgentRecipeItemContext.Instance()->ResultItemId),
            "GatheringNote" => GetGatheringNoteItemId(args),
            "ItemSearch" => NormalizeItemId((uint)AgentContext.Instance()->UpdateCheckerParam),
            "ChatLog" => GetChatLogItemId(),
            _ => null,
        };
    }

    private static unsafe uint? GetGatheringNoteItemId(IMenuOpenedArgs args)
    {
        var discriminator = *(byte*)(args.AgentPtr + Offsets.GatheringNoteContextDiscriminator);
        if (discriminator != 4)
        {
            return null;
        }

        return NormalizeItemId(AgentGatheringNote.Instance()->ContextMenuItemId);
    }

    private static unsafe uint? GetChatLogItemId()
    {
        var agent = AgentChatLog.Instance();

        if (*(uint*)((nint)(&agent->ContextItemId) + 8) != 3)
        {
            return null;
        }

        return NormalizeItemId(agent->ContextItemId);
    }

    private static uint NormalizeItemId(uint itemId)
    {
        if (itemId >= 1000000u)
        {
            itemId -= 1000000u;
        }
        else if (itemId >= 500000u)
        {
            itemId -= 500000u;
        }

        return itemId;
    }

    private static IGatherable? ResolveGatherable(uint itemId)
    {
        if (itemId == 0)
        {
            return null;
        }

        if (Diadem.ApprovedToRawItemIds.TryGetValue(itemId, out var rawItemId))
        {
            itemId = rawItemId;
        }

        if (GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var g))
        {
            return g;
        }

        return GatherBuddy.GameData.Fishes.GetValueOrDefault(itemId);
    }

    private unsafe uint? GetSatisfactionSupplyItemId()
    {
        var agent = AgentSatisfactionSupply.Instance();
        if (!agent->IsAgentActive())
        {
            return null;
        }

        var agentContext = AgentContext.Instance();
        if (agentContext->CurrentContextMenuTarget != null)
        {
            return null;
        }

        var itemIdx = agent->NpcInfo.SelectedItemIndex;
        if (itemIdx < 0 || itemIdx >= agent->Items.Length)
        {
            return null;
        }

        return NormalizeItemId(agent->Items[itemIdx].Id);
    }
}
