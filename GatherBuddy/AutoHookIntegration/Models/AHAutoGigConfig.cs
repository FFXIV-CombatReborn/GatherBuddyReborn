using System;
using System.Collections.Generic;

namespace GatherBuddy.AutoHookIntegration.Models;

public class AHAutoGigConfig
{
    public Guid UniqueId { get; set; } = Guid.NewGuid();
    public string PresetName { get; set; }
    public List<AHBaseGig> Gigs { get; set; } = [];
    public int HitboxSize { get; set; } = 25;

    public AHAutoGigConfig(string presetName)
    {
        PresetName = presetName;
    }
}

public class AHBaseGig
{
    public Guid UniqueId { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public int ItemId { get; set; }
    public bool UseNaturesBounty { get; set; }
    public float LeftOffset { get; set; }
    public float RightOffset { get; set; }

    public AHBaseGig(int itemId)
    {
        ItemId = itemId;
    }
}
