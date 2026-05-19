using System;

namespace GatherBuddy.Config;

[Flags]
public enum GatheredFilter
{
    None            = 0,
    AlreadyGathered = 0x01,
    Ungathered      = 0x02,
    NotTracked      = 0x04,
    UnknownLogState = 0x08,
    All             = AlreadyGathered | Ungathered | NotTracked | UnknownLogState,
}
