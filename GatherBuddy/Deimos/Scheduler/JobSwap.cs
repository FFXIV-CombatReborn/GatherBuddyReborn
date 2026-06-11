using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace GatherBuddy.Deimos.Scheduling;

/// simple gearset-based job switching
internal static unsafe class JobSwap
{
    public static bool TryEquip(uint jobId)
    {
        var gearsets = RaptureGearsetModule.Instance();
        if (gearsets == null)
            return false;

        for (var i = 0; i < 100; i++)
        {
            if (gearsets->Entries[i].ClassJob != jobId)
                continue;

            gearsets->EquipGearset(i);
            return true;
        }

        return false;
    }
}
