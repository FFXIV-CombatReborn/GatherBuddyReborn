using System.Collections.Immutable;

namespace GatherBuddy.Data;

internal static class UmbralNodes
{
    public enum UmbralWeatherType : uint
    {
        UmbralFlare = 133,
        UmbralDuststorms = 134,
        UmbralLevin = 135,
        UmbralTempest = 136,
    }

    public static readonly ImmutableArray<(uint BaseNodeId, UmbralWeatherType Weather)> UmbralNodeData =
    [
        (798, UmbralWeatherType.UmbralFlare),
        (799, UmbralWeatherType.UmbralLevin),
        (800, UmbralWeatherType.UmbralTempest),
        (801, UmbralWeatherType.UmbralDuststorms),
    ];
}
