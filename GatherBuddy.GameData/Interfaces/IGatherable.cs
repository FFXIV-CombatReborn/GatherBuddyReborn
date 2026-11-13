using GatherBuddy.Utility;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using Weather = GatherBuddy.Structs.Weather;

namespace GatherBuddy.Interfaces;

public enum ObjectType : byte
{
    Invalid,
    Gatherable,
    Fish,
}

public interface IGatherable
{
    MultiString Name { get; }
    IReadOnlyList<ILocation> Locations { get; }
    int InternalLocationId { get; }
    uint ItemId { get; }
    Item ItemData { get; }
    ObjectType Type { get; }
    bool IsCrystal { get; }
    bool IsTreasureMap { get; }
    Weather UmbralWeather { get; }
}
