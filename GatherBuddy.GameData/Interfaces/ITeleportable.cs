using GatherBuddy.Classes;

namespace GatherBuddy.Interfaces;

public interface ITeleportable
{
    Aetheryte? ClosestAetheryte { get; set; }

    Aetheryte? DefaultAetheryte { get; }


    bool SetAetheryte(Aetheryte? closestAetheryte)
    {
        if (closestAetheryte is { Id: 0 })
        {
            return false;
        }

        ClosestAetheryte = closestAetheryte;
        return true;
    }
}
