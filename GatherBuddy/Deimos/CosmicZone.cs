namespace GatherBuddy.Deimos;

/// cosmic zone ids + the gate that scopes Deimos to them
internal static class CosmicZone
{
    public const uint SinusArdorum = 1237;
    public const uint Phaenna      = 1291;
    public const uint Oizys        = 1310;
    public const uint Auxesia      = 1319;

    /// <summary> The player's current territory id. </summary>
    public static uint Current => Dalamud.ClientState.TerritoryType;

    public static bool InCosmicZone => Current is SinusArdorum or Phaenna or Oizys or Auxesia;

    public static string Name(uint territoryId) => territoryId switch
    {
        SinusArdorum => "Sinus Ardorum",
        Phaenna      => "Phaenna",
        Oizys        => "Oizys",
        Auxesia      => "Auxesia",
        _            => $"Zone {territoryId}",
    };
}
