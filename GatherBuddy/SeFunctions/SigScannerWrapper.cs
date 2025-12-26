using System;
using Dalamud.Plugin.Services;

namespace GatherBuddy.SeFunctions;

public interface ISigScannerWrapper
{
    IntPtr GetStaticAddressFromSig(string signature, int offset = 0);
    IntPtr ScanText(string signature);
    IntPtr ModuleBaseAddress { get; }
}

public class SigScannerWrapper : ISigScannerWrapper
{
    private readonly ISigScanner _sigScanner;

    public SigScannerWrapper(IGameInteropProvider interop)
    {
        _sigScanner = Dalamud.SigScanner;
    }

    public IntPtr GetStaticAddressFromSig(string signature, int offset = 0)
    {
        return (IntPtr)_sigScanner.GetStaticAddressFromSig(signature, offset);
    }

    public IntPtr ScanText(string signature)
    {
        return (IntPtr)_sigScanner.ScanText(signature);
    }

    public IntPtr ModuleBaseAddress => _sigScanner.Module.BaseAddress;
}
