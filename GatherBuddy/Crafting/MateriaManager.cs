using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace GatherBuddy.Crafting;

public static unsafe class MateriaManager
{
    public static bool IsExtractionUnlocked()
        => QuestManager.IsQuestComplete(66174);

    public static bool IsSpiritbondReadyAny()
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null)
            return false;

        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0)
                continue;
            if (slot->SpiritbondOrCollectability == 10000)
                return true;
        }
        return false;
    }

    public static int ReadySpiritbondItemCount()
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null)
            return 0;

        var count = 0;
        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0)
                continue;
            if (slot->SpiritbondOrCollectability == 10000)
                count++;
        }
        return count;
    }

    public static bool HasFreeInventorySlots()
    {
        var inv = InventoryManager.Instance();
        if (inv == null)
            return true;

        var containers = new[]
        {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        };

        foreach (var type in containers)
        {
            var container = inv->GetInventoryContainer(type);
            if (container == null)
                continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    return true;
            }
        }
        return false;
    }

    public static bool IsMateriaMenuOpen()
    {
        var ptr = Dalamud.GameGui.GetAddonByName("Materialize");
        return ptr.Address != nint.Zero;
    }

    public static bool IsMateriaDialogOpen()
    {
        var ptr = Dalamud.GameGui.GetAddonByName("MaterializeDialog");
        return ptr.Address != nint.Zero;
    }
}
