namespace GatherBuddy.Crafting;

/// cosmic-specific craft inputs passed into the Vulcan craft pipeline (primitives only, dormant otherwise)
public readonly record struct CosmicCraftContext(
    bool HasMaterialMiracle,
    uint MaterialMiracleCharges,
    bool HasSteadyHand,
    uint SteadyHandCharges,
    int  TargetQuality);
