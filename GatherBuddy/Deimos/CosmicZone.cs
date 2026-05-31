namespace GatherBuddy.Deimos;

/// cosmic zone ids + the gate that scopes Deimos to them
internal static class CosmicZone
{
    public const uint SinusArdorum = 1237;
    public const uint Phaenna      = 1291;
    public const uint Oizys        = 1310;

    // Reserved for the next moon; not yet gated on until confirmed as a cosmic territory.
    public const uint Moon4Reserved = 1321;

    /// <summary> The player's current territory id. </summary>
    public static uint Current => Dalamud.ClientState.TerritoryType;

    /// <summary> True while the player is standing in a known Cosmic Exploration zone. </summary>
    public static bool InCosmicZone => Current is SinusArdorum or Phaenna or Oizys;

    public static string Name(uint territoryId) => territoryId switch
    {
        SinusArdorum => "Sinus Ardorum",
        Phaenna      => "Phaenna",
        Oizys        => "Oizys",
        _            => $"Zone {territoryId}",
    };
}
