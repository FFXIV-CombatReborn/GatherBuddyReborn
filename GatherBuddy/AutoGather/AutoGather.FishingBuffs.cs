using System.Linq;
using GatherBuddy.Helpers;

namespace GatherBuddy.AutoGather;

public partial class AutoGather
{
    private static readonly uint[] FishingBuffIds =
    [
        850,   // Angler's Fortune (Patience)
        1803,  // Surface Slap
        1804,  // Identical Cast
        2779,  // Makeshift Bait
        2780,  // Prize Catch
        568,   // Fisher's Intuition
        763,   // Chum
        762,   // Fish Eyes
        3907,  // Big Game Fishing
        3972,  // Ambitious Lure
        3973   // Modest Lure
    ];

    private bool HasActiveFishingBuff()
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return false;

        return player.StatusList.Any(status => FishingBuffIds.Contains(status.StatusId));
    }

    private bool HasFoodBuff()
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return false;

        return player.StatusList.Any(status => status.StatusId == 48);
    }

    private bool HasMedicineBuff()
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return false;

        return player.StatusList.Any(status => status.StatusId == 49);
    }

    private bool TryUseFoodAndMedicine()
    {
        if (Player.Job != 18)
            return false;

        var config = GatherBuddy.Config.AutoGatherConfig;
        var needFood = config.UseFood && config.FoodItemId > 0 && !HasFoodBuff() && GetInventoryItemCount(config.FoodItemId) > 0;
        var needMed  = config.UseMedicine && config.MedicineItemId > 0 && !HasMedicineBuff() && GetInventoryItemCount(config.MedicineItemId) > 0;

        if (!needFood && !needMed)
            return false;

        if (HasActiveFishingBuff())
            return false;

        if (IsFishing || IsGathering)
        {
            GatherBuddy.Log.Debug("[Consumables] Quitting fishing to use food/medicine");
            QueueQuitFishingTasks();
            return true;
        }
        
        if (needFood)
        {
            var itemCount = GetInventoryItemCount(config.FoodItemId);
            if (itemCount > 0)
            {
                GatherBuddy.Log.Information($"[Consumables] Using food item {config.FoodItemId}");
                EnqueueActionWithDelay(() => UseItem(config.FoodItemId));
                return true;
            }
            else
            {
                GatherBuddy.Log.Warning($"[Consumables] Configured food item {config.FoodItemId} not found in inventory");
            }
        }

        if (needMed)
        {
            GatherBuddy.Log.Information($"[Consumables] Using medicine item {config.MedicineItemId}");
            EnqueueActionWithDelay(() => UseItem(config.MedicineItemId));
            return true;
        }

        return false;
    }
}
