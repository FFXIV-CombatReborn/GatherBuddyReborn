using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace GatherBuddy.Automation;

public static unsafe class Callback
{
    private static delegate* unmanaged<AtkUnitBase*, uint, AtkValue*, bool, bool> _fireCallbackPtr;

    private static void Initialize()
    {
        if (_fireCallbackPtr != null) return;
        
        _fireCallbackPtr = AtkUnitBase.MemberFunctionPointers.FireCallback;
        GatherBuddy.Log.Debug($"[Callback] Initialized using ClientStructs MemberFunctionPointers");
    }

    public static void Fire(AtkUnitBase* unitBase, bool updateState, params object[] values)
    {
        if (unitBase == null)
        {
            GatherBuddy.Log.Error("[Callback] Attempted to fire callback on null UnitBase");
            return;
        }

        if (_fireCallbackPtr == null)
            Initialize();

        var atkValues = (AtkValue*)Marshal.AllocHGlobal(values.Length * sizeof(AtkValue));
        if (atkValues == null)
        {
            GatherBuddy.Log.Error("[Callback] Failed to allocate memory for AtkValues");
            return;
        }

        try
        {
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                switch (value)
                {
                    case uint uintValue:
                        atkValues[i].Type = ValueType.UInt;
                        atkValues[i].UInt = uintValue;
                        break;
                    case int intValue:
                        atkValues[i].Type = ValueType.Int;
                        atkValues[i].Int = intValue;
                        break;
                    case float floatValue:
                        atkValues[i].Type = ValueType.Float;
                        atkValues[i].Float = floatValue;
                        break;
                    case bool boolValue:
                        atkValues[i].Type = ValueType.Bool;
                        atkValues[i].Byte = (byte)(boolValue ? 1 : 0);
                        break;
                    case string stringValue:
                    {
                        atkValues[i].Type = ValueType.String;
                        var stringBytes = Encoding.UTF8.GetBytes(stringValue);
                        var stringAlloc = Marshal.AllocHGlobal(stringBytes.Length + 1);
                        Marshal.Copy(stringBytes, 0, stringAlloc, stringBytes.Length);
                        Marshal.WriteByte(stringAlloc, stringBytes.Length, 0);
                        atkValues[i].String = (byte*)stringAlloc;
                        break;
                    }
                    case AtkValue rawValue:
                        atkValues[i] = rawValue;
                        break;
                    default:
                        GatherBuddy.Log.Error($"[Callback] Unable to convert type {value?.GetType()} to AtkValue");
                        throw new ArgumentException($"Unable to convert type {value?.GetType()} to AtkValue");
                }
            }

            var addonName = unitBase->NameString;
            GatherBuddy.Log.Verbose($"[Callback] Firing on {addonName}, valueCount={values.Length}, updateState={updateState}");

            _fireCallbackPtr(unitBase, (uint)values.Length, atkValues, updateState);
        }
        catch (Exception e)
        {
            GatherBuddy.Log.Error($"[Callback] Exception during callback fire: {e.Message}");
        }
        finally
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (atkValues[i].Type == ValueType.String)
                {
                    Marshal.FreeHGlobal(new IntPtr(atkValues[i].String));
                }
            }
            Marshal.FreeHGlobal(new IntPtr(atkValues));
        }
    }
}

public static unsafe class Chat
{
    private const string SanitizeStringSignature = "48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 70 4D 8B F8 4C 89 44 24 ?? 4C 8B 05 ?? ?? ?? ?? 44 8B E2";

    private delegate void SanitizeStringDelegate(Utf8String* stringPtr, int a2, nint a3);

    private static SanitizeStringDelegate? _sanitizeString;


    private static void InitializeSanitizeString()
    {
        if (_sanitizeString != null) return;
        
        var addr = (IntPtr)Dalamud.SigScanner.ScanText(SanitizeStringSignature);
        _sanitizeString = Marshal.GetDelegateForFunctionPointer<SanitizeStringDelegate>(addr);
        GatherBuddy.Log.Debug($"[Chat] SanitizeString initialized at 0x{addr:X16}");
    }

    private static string SanitizeText(string text)
    {
        InitializeSanitizeString();
        
        var uText = Utf8String.FromString(text);
        _sanitizeString!(uText, 0x27F, IntPtr.Zero);
        var sanitized = uText->ToString();
        uText->Dtor();
        IMemorySpace.Free(uText);
        return sanitized;
    }

    private static void SendMessageUnsafe(byte[] message)
    {
        var uiModule = UIModule.Instance();
        var utf8String = Utf8String.FromString(new string(Encoding.UTF8.GetChars(message)));
        uiModule->ProcessChatBoxEntry(utf8String);
        utf8String->Dtor();
        IMemorySpace.Free(utf8String);
        GatherBuddy.Log.Debug($"[Chat] Message sent");
    }

    public static void SendMessage(string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        
        if (bytes.Length == 0)
        {
            GatherBuddy.Log.Error("[Chat] Attempted to send empty message");
            throw new ArgumentException("message is empty", nameof(message));
        }
        
        if (bytes.Length > 500)
        {
            GatherBuddy.Log.Error($"[Chat] Attempted to send message longer than 500 bytes: {bytes.Length}");
            throw new ArgumentException("message is longer than 500 bytes", nameof(message));
        }
        
        if (message.Length != SanitizeText(message).Length)
        {
            GatherBuddy.Log.Error("[Chat] Message contained invalid characters");
            throw new ArgumentException("message contained invalid characters", nameof(message));
        }
        
        if (message.Contains('\n'))
        {
            GatherBuddy.Log.Error("[Chat] Message cannot contain newlines");
            throw new ArgumentException("message can't contain multiple lines", nameof(message));
        }
        
        if (message.Contains('\r'))
        {
            GatherBuddy.Log.Error("[Chat] Message cannot contain carriage return");
            throw new ArgumentException("message can't contain carriage return", nameof(message));
        }

        SendMessageUnsafe(bytes);
    }

    public static void ExecuteCommand(string message)
    {
        if (!message.StartsWith("/"))
        {
            GatherBuddy.Log.Error($"[Chat] Attempted to execute command without slash prefix: {message}");
            throw new InvalidOperationException($"Attempted to execute command but was not prefixed with a slash: {message}");
        }
        
        SendMessage(message);
    }

    [Obsolete("Use Chat.ExecuteCommand directly")]
    public static class Instance
    {
        public static void ExecuteCommand(string message) => Chat.ExecuteCommand(message);
    }
}
