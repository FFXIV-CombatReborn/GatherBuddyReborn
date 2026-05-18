using System;

namespace GatherBuddy.Config;

[Flags]
public enum LevelingFilter
{
    None        = 0,
    Leveling    = 0x01,
    NonLeveling = 0x02,
    All         = Leveling | NonLeveling,
}
