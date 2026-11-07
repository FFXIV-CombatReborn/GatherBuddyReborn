using GatherBuddy.Classes;
using GatherBuddy.Structs;

namespace GatherBuddy.Interfaces;

public interface IMarkable
{
    const int CoordMin = 100;
    const int CoordMax = 4200;
    const ushort RadiusMax = 400;

    string Name { get; }
    Territory Territory { get; }
    int IntegralXCoord { get; set; }
    int IntegralYCoord { get; set; }

    int DefaultXCoord { get; }
    int DefaultYCoord { get; }

    ushort Radius { get; set; }
    ushort DefaultRadius { get; }

    WaymarkSet Markers { get; set; }

    bool SetMarkers(WaymarkSet markers)
    {
        if (Markers.Equals(markers))
        {
            return false;
        }

        Markers = markers;
        return true;
    }

    bool SetXCoord(int xCoord)
    {
        if (xCoord is < CoordMin or > CoordMax)
        {
            return false;
        }

        IntegralXCoord = xCoord;
        return true;
    }

    bool SetYCoord(int yCoord)
    {
        if (yCoord is < CoordMin or > CoordMax)
        {
            return false;
        }

        IntegralYCoord = yCoord;
        return true;
    }

    bool SetRadius(ushort radius)
    {
        if (radius is 0 or > RadiusMax)
        {
            return false;
        }

        Radius = radius;
        return true;
    }
}
