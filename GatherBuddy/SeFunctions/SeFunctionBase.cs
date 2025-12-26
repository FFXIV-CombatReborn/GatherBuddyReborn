using System;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace GatherBuddy.SeFunctions;

public class SeFunctionBase<T> where T : Delegate
{
    public    IntPtr Address;
    protected T?     FuncDelegate;

    public SeFunctionBase(ISigScanner sigScanner, int offset)
    {
        Address = sigScanner.Module.BaseAddress + offset;
        GatherBuddy.Log.Debug($"{GetType().Name} address 0x{Address.ToInt64():X16}, baseOffset 0x{offset:X16}.");
    }

    public SeFunctionBase(ISigScanner sigScanner, string signature, int offset = 0)
    {
        if (sigScanner.TryScanText(signature, out var ptr))
            Address = (IntPtr)ptr + offset;
        else
            Address = IntPtr.Zero;
        
        var baseOffset = Address != IntPtr.Zero ? (ulong)Address.ToInt64() - (ulong)sigScanner.Module.BaseAddress.ToInt64() : 0;
        GatherBuddy.Log.Debug($"{GetType().Name} address 0x{Address.ToInt64():X16}, baseOffset 0x{baseOffset:X16}.");
    }

    public T? Delegate()
    {
        if (FuncDelegate != null)
            return FuncDelegate;

        if (Address != IntPtr.Zero)
        {
            FuncDelegate = Marshal.GetDelegateForFunctionPointer<T>(Address);
            return FuncDelegate;
        }

        GatherBuddy.Log.Error($"Trying to generate delegate for {GetType().Name}, but no pointer available.");
        return null;
    }

    public dynamic? Invoke(params dynamic[] parameters)
    {
        if (FuncDelegate != null)
            return FuncDelegate.DynamicInvoke(parameters);

        if (Address != IntPtr.Zero)
        {
            FuncDelegate = Marshal.GetDelegateForFunctionPointer<T>(Address);
            return FuncDelegate!.DynamicInvoke(parameters);
        }
        else
        {
            GatherBuddy.Log.Error($"Trying to call {GetType().Name}, but no pointer available.");
            return null;
        }
    }

    public Hook<T>? CreateHook(IGameInteropProvider provider, T detour)
    {
        if (Address != IntPtr.Zero)
        {
            var hook = provider.HookFromAddress(Address, detour);
            hook.Enable();
            GatherBuddy.Log.Debug($"Hooked onto {GetType().Name} at address 0x{Address.ToInt64():X16}.");
            return hook;
        }

        GatherBuddy.Log.Error($"Trying to create Hook for {GetType().Name}, but no pointer available.");
        return null;
    }
}
