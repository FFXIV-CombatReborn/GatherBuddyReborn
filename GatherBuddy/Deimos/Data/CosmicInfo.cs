using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.Deimos.Data;

/// one craftable in a mission (recipe + how many + inputs)
public sealed class CraftingInfo
{
    public uint                 ItemId;
    public int                  RequiredAmount;
    public uint                 RecipeId;
    public bool                 ExpertCraft;
    public Dictionary<uint, int> RequiredItems = new();
    public int                  IconId;
    public string               ItemName = "???";
}

/// sheet-derived data for a single cosmic mission
public sealed class CosmicInfo
{
    // crafter
    public Dictionary<ushort, CraftingInfo> Crafts_Main = new();
    public Dictionary<ushort, CraftingInfo> Crafts_Pre  = new();
    public bool                             IsExpert;

    // gatherer
    public Dictionary<uint, int> Gathering_Min = new();

    // fisher
    public int          Fish_AmountRequired;
    public int          Fish_VarietyAmount;
    public List<string> Fish_Presets = new();

    // map
    public Vector2 MapPosition;
    public int     Radius;
    public uint    TerritoryId;
    public uint    MarkerId;

    // exp modifiers
    public uint ExpModifier_1;
    public uint ExpModifier_2;
    public uint ExpModifier_3;

    // universal
    public string             Name = "";
    public List<uint>         Jobs = new();
    public uint               ToDoId;
    public uint               Rank = 1;
    public uint               Level;
    public MissionAttributes  Attributes;
    public CosmicWeather       Weather;
    public uint               StartTime;
    public uint               EndTime;
    public uint               ClassScore;
    public uint               CosmoCredit;
    public uint               LunarCredit;
    public uint               RewardItem;
    public uint               RewardItemAmount;
    public uint               DronebitReward;
    public uint               PreviousMissionId;
    public Dictionary<int, int> RelicXpInfo = new();
    public uint               BronzeScore;
    public uint               SilverScore;
    public uint               GoldScore;
    public uint               TemporaryActionId;
    public uint               TemporaryActionCount;
    public MissionStatus      CompletionStatus = MissionStatus.None;
    public List<uint>         SequenceMissions_Previous = new();
    public List<uint>         SequenceMissions_Next     = new();

    public bool IsProvisional => Attributes.HasFlag(MissionAttributes.ProvisionalWeather)
                              || Attributes.HasFlag(MissionAttributes.ProvisionalSequential)
                              || Attributes.HasFlag(MissionAttributes.ProvisionalTimed);
    public bool IsCritical => Attributes.HasFlag(MissionAttributes.Critical);
    public bool IsWeather  => Attributes.HasFlag(MissionAttributes.ProvisionalWeather);
    public bool IsTimed    => Attributes.HasFlag(MissionAttributes.ProvisionalTimed);
    public bool IsSequence => Attributes.HasFlag(MissionAttributes.ProvisionalSequential);
    public bool IsCraftOnly => Attributes.HasFlag(MissionAttributes.Craft)
                            && !Attributes.HasFlag(MissionAttributes.Gather)
                            && !Attributes.HasFlag(MissionAttributes.Fish);
    public bool ARank => Rank is 5 or 4;
    public bool BRank => Rank is 3;
    public bool CRank => Rank is 2;
    public bool DRank => Rank is 1;
}
