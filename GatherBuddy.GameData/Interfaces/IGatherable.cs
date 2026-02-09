using System.Collections.Generic;
using GatherBuddy.Classes;
using GatherBuddy.Utility;
using Lumina.Excel.Sheets;
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
    public MultiString              Name               { get; }
    public IReadOnlyList<ILocation> Locations          { get; }
    public int                      InternalLocationId { get; }
    public uint                     ItemId             { get; }
    public Item                     ItemData           { get; }
    public ObjectType               Type               { get; }
    public bool                     IsCrystal          { get; }
    public bool                     IsTreasureMap      { get; }
    public Weather                  UmbralWeather      { get; }
}
