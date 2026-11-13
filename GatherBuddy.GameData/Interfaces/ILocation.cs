using GatherBuddy.Enums;
using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.Interfaces;

public interface ILocation : IMarkable, ITeleportable
{
    uint Id { get; }
    ObjectType Type { get; }
    GatheringType GatheringType { get; }
    IEnumerable<IGatherable> Gatherables { get; }
    Dictionary<uint, List<Vector3>> WorldPositions { get; }
    int AetheryteDistance()
        => ClosestAetheryte?.WorldDistance(Territory.Id, IntegralXCoord, IntegralYCoord) ?? int.MaxValue;

}
