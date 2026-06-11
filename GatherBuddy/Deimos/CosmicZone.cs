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

    /// red-alert collection point object ids
    public const uint CollectionPointA = 2014616;
    public const uint CollectionPointB = 2014618;

    public static System.Numerics.Vector3? HubCenter(uint territoryId) => territoryId switch
    {
        SinusArdorum => new System.Numerics.Vector3(2.84f, 1.55f, -0.06f),
        Phaenna      => new System.Numerics.Vector3(339.90f, 52.60f, -412.10f),
        Oizys        => new System.Numerics.Vector3(-180.02f, 0.50f, 129.25f),
        Auxesia      => new System.Numerics.Vector3(291.00f, 205.78f, 376.02f),
        _            => null,
    };

    /// cosmo credits are one shared item; lunar credits differ per moon
    public const uint CosmoCreditItem = 45690;

    public static uint LunarCreditItem(uint territoryId) => territoryId switch
    {
        SinusArdorum => 45691,
        Phaenna      => 48146,
        Oizys        => 48147,
        Auxesia      => 48148,
        _            => 0,
    };

    public static string Name(uint territoryId) => territoryId switch
    {
        SinusArdorum => "Sinus Ardorum",
        Phaenna      => "Phaenna",
        Oizys        => "Oizys",
        Auxesia      => "Auxesia",
        _            => $"Zone {territoryId}",
    };
}
