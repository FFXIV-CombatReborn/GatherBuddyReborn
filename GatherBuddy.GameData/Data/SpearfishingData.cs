using System.Linq;

namespace GatherBuddy.Data;

public static class SpearfishingData
{
    public static void Apply(GameData data)
    {
        foreach (var fish in data.Fishes.Values.Where(fish => fish.IsSpearFish && fish.Predators.Length > 0))
        {
            foreach (var (requiredFish, _) in fish.Predators)
            {
                var parentSpot = requiredFish.FishingSpots.FirstOrDefault(spot => spot.Spearfishing && !spot.IsShadowNode);
                if (parentSpot == null)
                {
                    continue;
                }

                foreach (var shadowSpot in fish.FishingSpots.Where(spot => spot.Spearfishing && spot.IsShadowNode && spot.ParentNode == null))
                {
                    shadowSpot.ParentNode = parentSpot;
                }
            }
        }
    }
}
