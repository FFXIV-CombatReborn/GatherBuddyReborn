using Dalamud.Game.ClientState.Keys;
using Newtonsoft.Json;
using System;

namespace GatherBuddy.Config;

[method: JsonConstructor]
public readonly struct ModifierHotkey(VirtualKey modifier) : IEquatable<ModifierHotkey>
{
    public static readonly ModifierHotkey NoKey = new(VirtualKey.NO_KEY);
    public static readonly ModifierHotkey Shift = new(VirtualKey.SHIFT);
    public static readonly ModifierHotkey Control = new(VirtualKey.CONTROL);
    public static readonly ModifierHotkey Alt = new(VirtualKey.MENU);

    public static readonly ModifierHotkey[] ValidModifiers =
    [
        VirtualKey.NO_KEY,
        VirtualKey.CONTROL,
        VirtualKey.MENU,
        VirtualKey.SHIFT,
    ];

    public readonly VirtualKey Modifier = modifier switch
    {
        VirtualKey.CONTROL or VirtualKey.LCONTROL or VirtualKey.RCONTROL => VirtualKey.CONTROL,
        VirtualKey.MENU or VirtualKey.LMENU or VirtualKey.RMENU => VirtualKey.MENU,
        VirtualKey.SHIFT or VirtualKey.LSHIFT or VirtualKey.RSHIFT => VirtualKey.SHIFT,
        _ => VirtualKey.NO_KEY,
    };

    public static implicit operator VirtualKey(ModifierHotkey key)
        => key.Modifier;

    public static implicit operator ModifierHotkey(VirtualKey key)
        => new(key);

    public bool Equals(ModifierHotkey other)
        => Modifier == other.Modifier;

    public override bool Equals(object? obj)
        => obj is ModifierHotkey other && Equals(other);

    public override int GetHashCode()
        => (int)Modifier;

    public static bool operator ==(ModifierHotkey lhs, ModifierHotkey rhs)
        => lhs.Modifier == rhs.Modifier;

    public static bool operator !=(ModifierHotkey lhs, ModifierHotkey rhs)
        => lhs.Modifier != rhs.Modifier;

    public override string ToString()
        => Modifier.GetFancyName();
}

public struct ModifiableHotkey : IEquatable<ModifiableHotkey>
{
    public VirtualKey Hotkey { get; private set; } = VirtualKey.NO_KEY;
    public ModifierHotkey Modifier1 { get; private set; } = ModifierHotkey.NoKey;
    public ModifierHotkey Modifier2 { get; private set; } = ModifierHotkey.NoKey;

    public ModifiableHotkey()
    { }

    public ModifiableHotkey(VirtualKey hotkey)
        => SetHotkey(hotkey);

    public ModifiableHotkey(VirtualKey hotkey, ModifierHotkey modifier1)
    {
        SetHotkey(hotkey);
        SetModifier1(modifier1);
    }

    [JsonConstructor]
    public ModifiableHotkey(VirtualKey hotkey, ModifierHotkey modifier1, ModifierHotkey modifier2)
    {
        SetHotkey(hotkey);
        SetModifier1(modifier1);
        SetModifier2(modifier2);
    }

    public bool SetHotkey(VirtualKey key)
    {
        if (Hotkey == key)
        {
            return false;
        }

        if (key == VirtualKey.NO_KEY)
        {
            Modifier1 = ModifierHotkey.NoKey;
            Modifier2 = ModifierHotkey.NoKey;
        }

        Hotkey = key;
        return true;
    }

    public bool SetModifier1(ModifierHotkey key)
    {
        if (Modifier1 == key)
        {
            return false;
        }

        if (key == ModifierHotkey.NoKey || key == Modifier2)
        {
            Modifier2 = ModifierHotkey.NoKey;
        }

        Modifier1 = key;
        return true;
    }

    public bool SetModifier2(ModifierHotkey key)
    {
        if (Modifier2 == key)
        {
            return false;
        }

        Modifier2 = Modifier1 == key ? ModifierHotkey.NoKey : key;
        return true;
    }

    public readonly bool Equals(ModifiableHotkey other)
        => Hotkey == other.Hotkey
         && Modifier1 == other.Modifier1
         && Modifier2 == other.Modifier2;

    public override readonly bool Equals(object? obj)
        => obj is ModifiableHotkey other && Equals(other);

    public override readonly int GetHashCode()
        => HashCode.Combine((int)Hotkey, Modifier1, Modifier2);

    public static bool operator ==(ModifiableHotkey lhs, ModifiableHotkey rhs)
        => lhs.Equals(rhs);

    public static bool operator !=(ModifiableHotkey lhs, ModifiableHotkey rhs)
        => !lhs.Equals(rhs);

    public override readonly string ToString()
        => Hotkey is VirtualKey.NO_KEY
            ? "No Key"
            : Modifier1.Modifier is VirtualKey.NO_KEY
                ? Hotkey.GetFancyName()
                : Modifier2.Modifier is VirtualKey.NO_KEY
                    ? $"{Modifier1} + {Hotkey.GetFancyName()}"
                    : $"{Modifier1} + {Modifier2} + {Hotkey.GetFancyName()}";
}
