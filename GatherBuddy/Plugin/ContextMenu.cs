using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.Classes;
using GatherBuddy.Interfaces;

namespace GatherBuddy.Plugin;

public class ContextMenu : IDisposable
{
    private readonly IContextMenu _contextMenu;
    private readonly Executor     _executor;
    private          IGatherable? _lastGatherable;
    private          uint?        _lastRecipeId;
    private          uint?        _lastVendorBuyListItemId;
    private          GatherBuddy  _plugin;

    private readonly MenuItem _menuItem;
    private readonly MenuItem _menuItemAuto;
    private readonly MenuItem _menuItemCrafting;
    private readonly MenuItem _menuItemVulcanRecipe;
    private readonly MenuItem _menuItemVendorBuyList;

    public ContextMenu(GatherBuddy plugin, IContextMenu menu, Executor executor)
    {
        _plugin = plugin;
        _contextMenu = menu;
        _executor    = executor;

        _menuItem = new MenuItem
        {
            IsEnabled   = true,
            IsReturn    = false,
            PrefixChar  = 'G',
            Name        = "Gather Manually",
            OnClicked   = OnClick,
            IsSubmenu   = false,
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
            PrefixChar = 'C',
            Name = "Add to Crafting List",
            OnClicked = OnClickCrafting,
            IsSubmenu = true,
            PrefixColor = 42,
        };

        _menuItemVulcanRecipe = new MenuItem
        {
            IsEnabled   = true,
            IsReturn    = false,
            PrefixChar  = 'V',
            Name        = "Open in Vulcan",
            OnClicked   = OnClickVulcanRecipe,
            IsSubmenu   = false,
            PrefixColor = 42,
        };

        _menuItemVendorBuyList = new MenuItem
        {
            IsEnabled   = true,
            IsReturn    = false,
            PrefixChar  = 'V',
            Name        = "Add to Vendor Buy List",
            OnClicked   = OnClickVendorBuyList,
            IsSubmenu   = false,
            PrefixColor = 42,
        };

        if (GatherBuddy.Config.AddIngameContextMenus)
            Enable();
    }

    private void OnClick(IMenuItemClickedArgs args)
    {
        if (_lastGatherable != null)
            _executor.GatherItem(_lastGatherable);
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

        GatherBuddy.Log.Debug($"[ContextMenu] Opening Vulcan to recipe for item {recipe.Value.ItemResult.RowId}");
        vulcanWindow.OpenToRecipe(recipe.Value.ItemResult.RowId);
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

            var recentLists = allLists
                .OrderByDescending(l => l.CreatedAt)
                .Take(maxLists)
                .ToList();

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
            args.OpenSubmenu(menuItems);
    }

    private void OnClickVendorBuyList(IMenuItemClickedArgs args)
    {
        if (!_lastVendorBuyListItemId.HasValue)
        {
            GatherBuddy.Log.Debug("[ContextMenu] Vendor buy-list context menu clicked without a cached item id.");
            return;
        }

        if (!GatherBuddy.VendorBuyListManager.TryIncrementTarget(_lastVendorBuyListItemId.Value, 1, true, true))
            GatherBuddy.Log.Debug($"[ContextMenu] Unable to add item {_lastVendorBuyListItemId.Value} to the vendor buy list.");
    }
    private void OpenCreateCraftingListPopup(uint recipeId)
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

    private void AddRecipeToList(Lumina.Excel.Sheets.Recipe recipe, Crafting.CraftingListDefinition list)
    {
        var existingItem = list.Recipes.FirstOrDefault(x => x.RecipeId == recipe.RowId);
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
            _lastRecipeId = GetRecipeIdFromContext(args);
        if (contextItemId.HasValue && GatherBuddy.VendorBuyListManager.CanAddGilShopItem(contextItemId.Value))
            _lastVendorBuyListItemId = contextItemId.Value;

        if (_lastGatherable != null)
            args.AddMenuItem(_menuItem);
        if (_lastGatherable is Gatherable)
            args.AddMenuItem(_menuItemAuto);
        if (_lastRecipeId.HasValue)
        {
            args.AddMenuItem(_menuItemCrafting);
            args.AddMenuItem(_menuItemVulcanRecipe);
        }
        if (_lastVendorBuyListItemId.HasValue)
            args.AddMenuItem(_menuItemVendorBuyList);
    }

    private unsafe uint? GetRecipeIdFromContext(IMenuOpenedArgs args)
    {
        var itemId = GetContextItemId(args);
        if (!itemId.HasValue)
            return null;

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
            null                 => GetSatisfactionSupplyItemId(),
            "ContentsInfoDetail" => GetGameObjectItemId("ContentsInfo", Offsets.ContentsInfoDetailContextItemId),
            "RecipeNote"         => GetGameObjectItemId("RecipeNote", Offsets.RecipeNoteContextItemId),
            "RecipeTree"         => GetGameObjectItemId(AgentById(AgentId.RecipeItemContext), Offsets.AgentItemContextItemId),
            "RecipeMaterialList" => GetGameObjectItemId(AgentById(AgentId.RecipeItemContext), Offsets.AgentItemContextItemId),
            "GatheringNote"      => GetGatheringNoteItemId(args),
            "ItemSearch"         => NormalizeItemId((uint)AgentContext.Instance()->UpdateCheckerParam),
            "ChatLog"            => GetGameObjectItemId("ChatLog", Offsets.ChatLogContextItemId, ValidateChatLogContext),
            _                    => null,
        };
    }

    private static unsafe uint? GetGatheringNoteItemId(IMenuOpenedArgs args)
    {
        var agent = Dalamud.GameGui.FindAgentInterface("GatheringNote");
        if (agent == IntPtr.Zero)
            return null;
        var discriminator = *(byte*)(args.AgentPtr + Offsets.GatheringNoteContextDiscriminator);
        if (discriminator != 4)
            return null;
        return NormalizeItemId(*(uint*)(agent + Offsets.GatheringNoteContextItemId));
    }

    private static uint NormalizeItemId(uint itemId)
    {
        if (itemId >= 1000000u)
            itemId -= 1000000u;
        else if (itemId >= 500000u)
            itemId -= 500000u;

        return itemId;
    }

    private static IGatherable? ResolveGatherable(uint itemId)
    {
        if (itemId == 0)
            return null;

        if (Diadem.ApprovedToRawItemIds.TryGetValue(itemId, out var rawItemId))
            itemId = rawItemId;

        if (GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var g))
            return g;

        return GatherBuddy.GameData.Fishes.GetValueOrDefault(itemId);
    }

    private static IGatherable? HandleItem(uint itemId)
        => ResolveGatherable(NormalizeItemId(itemId));

    private unsafe uint? GetGameObjectItemId(IntPtr agent, int offset, Func<nint, bool> validate)
        => agent != IntPtr.Zero && validate(agent) ? NormalizeItemId(*(uint*)(agent + offset)) : null;

    private unsafe uint? GetGameObjectItemId(IntPtr agent, int offset)
        => agent != IntPtr.Zero ? NormalizeItemId(*(uint*)(agent + offset)) : null;

    private uint? GetGameObjectItemId(string name, int offset, Func<nint, bool> validate)
        => GetGameObjectItemId(Dalamud.GameGui.FindAgentInterface(name), offset, validate);

    private uint? GetGameObjectItemId(string name, int offset)
        => GetGameObjectItemId(Dalamud.GameGui.FindAgentInterface(name), offset);

    private unsafe uint? GetSatisfactionSupplyItemId()
    {
        var agent = Dalamud.GameGui.FindAgentInterface("SatisfactionSupply");
        if (agent == IntPtr.Zero)
            return null;

        var itemIdx = *(byte*)(agent + Offsets.SatisfactionSupplyItemIdx);
        return itemIdx switch
        {
            1 => NormalizeItemId(*(uint*)(agent + Offsets.SatisfactionSupplyItem1Id)),
            2 => NormalizeItemId(*(uint*)(agent + Offsets.SatisfactionSupplyItem2Id)),
            _ => null,
        };
    }

    private static unsafe bool ValidateChatLogContext(nint agent)
        => *(uint*)(agent + Offsets.ChatLogContextItemId + 8) == 3;

    private static unsafe IntPtr AgentById(AgentId id)
    {
        var uiModule = (UIModule*)Dalamud.GameGui.GetUIModule().Address;
        var agents   = uiModule->GetAgentModule();
        var agent    = agents->GetAgentByInternalId(id);
        return (IntPtr)agent;
    }
}
