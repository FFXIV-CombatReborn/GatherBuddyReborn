using GatherBuddy.Enums;
using System;

namespace GatherBuddy.SeFunctions;

public sealed class SeTugType : SeAddressBase
{
    public SeTugType(ISigScannerWrapper sigScanner)
        : base(sigScanner,
            "48 8D 35 ?? ?? ?? ?? 4C 8B CE")
    { }

    public unsafe BiteType Bite
        => Address != IntPtr.Zero ? *(BiteType*)Address : BiteType.Unknown;
}
