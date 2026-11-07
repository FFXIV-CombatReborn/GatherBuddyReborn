using GatherBuddy.AutoHookIntegration.Models;
using GatherBuddy.Classes;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.AutoHookIntegration;

public class AutoHookSpearfishingPresetBuilder
{
    public static AHAutoGigConfig BuildSpearfishingPreset(string presetName, IEnumerable<Fish> fishList)
    {
        var preset = new AHAutoGigConfig(presetName);
        var addedFish = new HashSet<uint>();

        foreach (var fish in fishList.Where(f => f.IsSpearFish))
        {
            if (addedFish.Add(fish.ItemId))
            {
                var gig = new AHBaseGig((int)fish.ItemId)
                {
                    Enabled = true,
                    UseNaturesBounty = false,
                    LeftOffset = 0,
                    RightOffset = 0
                };
                preset.Gigs.Add(gig);
            }
        }

        return preset;
    }
}
