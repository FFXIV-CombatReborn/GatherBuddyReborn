using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using GatherBuddy.Config;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.RebornUI.Settings;

internal static class SettingRows
{
    internal static readonly M3Segment[] AboveBelowSegments =
    [
        new("Above", FontAwesomeIcon.ArrowUp),
        new("Below", FontAwesomeIcon.ArrowDown),
    ];

    private static readonly string[] ModifierLabels = Array.ConvertAll(ModifierHotkey.ValidModifiers, m => m.Modifier == VirtualKey.NO_KEY ? "None" : m.ToString());

    private static void Save()
        => GatherBuddy.Config.Save();

    public static bool Switch(string label, string? supporting, bool current, Action<bool> setter, bool enabled = true)
    {
        var value = current;
        if (!M3Widgets.RowSwitch(label, ref value, supporting, enabled) || value == current)
        {
            return false;
        }

        setter(value);
        Save();
        return true;
    }

    public static bool PluginSwitch(string label, string? supporting, bool current, Action<bool> setter, params M3PluginRequirement[] plugins)
        => PluginSwitch(label, supporting, current, setter, false, true, plugins);

    public static bool PluginSwitch(string label, string? supporting, bool current, Action<bool> setter, bool any, bool enabled,
        params M3PluginRequirement[] plugins)
    {
        var value = current;
        if (!M3Widgets.RowPluginSwitch(label, ref value, plugins, supporting, any, enabled) || value == current)
        {
            return false;
        }

        setter(value);
        Save();
        return true;
    }

    public static bool Number(string label, string? supporting, int current, int min, int max, Action<int> setter, int step = 1,
        string? unit = null, bool enabled = true)
    {
        var value = current;
        if (!M3Widgets.RowNumber(label, ref value, step, min, max, unit, supporting, enabled))
        {
            return false;
        }

        value = Math.Clamp(value, min, max);
        if (value == current)
        {
            return false;
        }

        setter(value);
        Save();
        return true;
    }

    public static bool Number(string label, string? supporting, float current, float min, float max, Action<float> setter, float step,
        string format = "%.1f", string? unit = null, bool enabled = true)
    {
        var value = current;
        if (!M3Widgets.RowNumber(label, ref value, step, min, max, format, unit, supporting, enabled))
        {
            return false;
        }

        value = Math.Clamp(value, min, max);
        if (value == current)
        {
            return false;
        }

        setter(value);
        Save();
        return true;
    }

    public static bool Slider(string label, string? supporting, int current, int min, int max, Action<int> setter, int step = 0, bool enabled = true)
    {
        var value = current;
        if (!M3Widgets.RowDragInt(label, ref value, min, max, supporting, enabled: enabled, step: step))
        {
            return false;
        }

        value = Math.Clamp(value, min, max);
        if (value == current)
        {
            return false;
        }

        setter(value);
        Save();
        return true;
    }

    public static bool Slider(string label, string? supporting, float current, float min, float max, string format, Action<float> setter,
        float step = 0f, bool enabled = true)
    {
        var value = current;
        if (!M3Widgets.RowDragFloat(label, ref value, min, max, format, supporting, enabled: enabled, step: step))
        {
            return false;
        }

        value = Math.Clamp(value, min, max);
        if (value == current)
        {
            return false;
        }

        setter(value);
        Save();
        return true;
    }

    public static bool Combo<T>(string label, string? supporting, T current, Action<T> setter, Func<T, string>? itemLabel = null, bool search = false,
        bool enabled = true) where T : struct, Enum
    {
        var value = current;
        if (!M3Widgets.RowCombo(label, ref value, supporting, itemLabel, search, enabled) || EqualityComparer<T>.Default.Equals(value, current))
        {
            return false;
        }

        setter(value);
        Save();
        return true;
    }

    public static bool Combo(string label, string? supporting, int current, IReadOnlyList<string> items, Action<int> setter, bool search = false,
        bool enabled = true)
    {
        var index = current;
        if (!M3Widgets.RowCombo(label, ref index, items, supporting, search, enabled) || index == current || index < 0 || index >= items.Count)
        {
            return false;
        }

        setter(index);
        Save();
        return true;
    }

    public static bool AboveBelow(string label, string? supporting, bool above, Action<bool> setter)
    {
        var index = above ? 0 : 1;
        if (!M3Widgets.RowSegmented(label, ref index, AboveBelowSegments, supporting) || index == (above ? 0 : 1))
        {
            return false;
        }

        setter(index == 0);
        Save();
        return true;
    }

    public static bool Text(string label, string? supporting, string current, Action<string> setter, string? hint = null, int maxLength = 256,
        bool enabled = true)
    {
        var value = current;
        if (!M3Widgets.RowText(label, ref value, hint, supporting, maxLength, enabled) || value == current)
        {
            return false;
        }

        setter(value);
        Save();
        return true;
    }

    public static bool FormatText(string label, string? supporting, string current, string defaultValue, Action<string> setter)
    {
        var (display, id) = SplitLabel(label);
        var row = M3SettingRow.Begin(display, supporting, Vector2.Zero);
        M3SettingRow.End(row);

        var resetSize = M3Widgets.IconButtonSize;
        var width = MathF.Max(64f * M3.Scale, ImGui.GetContentRegionAvail().X - M3Card.RightInset - resetSize - M3.Space1);
        var start = ImGui.GetCursorScreenPos();
        var value = current;
        var changed = M3TextField.Draw($"##{id}_format", "Leave empty for no chat message", ref value, width);
        var fieldHeight = M3TextField.Height;
        ImGui.SetCursorScreenPos(new Vector2(start.X + width + M3.Space1, start.Y + (fieldHeight - resetSize) * 0.5f));
        if (M3Widgets.IconButton($"##{id}_reset", FontAwesomeIcon.Undo, $"Reset to the default:\n{defaultValue}", enabled: current != defaultValue))
        {
            value = defaultValue;
            changed = true;
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + MathF.Max(fieldHeight, resetSize)));
        ImGui.Dummy(new Vector2(width, M3.Space2));

        if (!changed || value == current)
        {
            return false;
        }

        setter(value);
        Save();
        return true;
    }

    public static bool Hotkey(string label, string? supporting, ModifiableHotkey current, Action<ModifiableHotkey> setter)
    {
        var value = ToM3(current);
        if (!M3Widgets.RowHotkey(label, ref value, supporting))
        {
            return false;
        }

        var hotkey = FromM3(value);
        if (hotkey == current)
        {
            return false;
        }

        setter(hotkey);
        Save();
        return true;
    }

    public static bool HoldKey(string label, string? supporting, VirtualKey current, VirtualKey[] allowed, Action<VirtualKey> setter)
    {
        var value = current == VirtualKey.NO_KEY ? M3Hotkey.None : new M3Hotkey(current);
        if (!M3Widgets.RowHotkey(label, ref value, supporting, modifiers: false, modifierKeys: true) || value.Key == current
         || Array.IndexOf(allowed, value.Key) < 0)
        {
            return false;
        }

        setter(value.Key);
        Save();
        return true;
    }

    public static bool Modifier(string label, string? supporting, ModifierHotkey current, Action<ModifierHotkey> setter)
    {
        var index = Math.Max(0, Array.IndexOf(ModifierHotkey.ValidModifiers, current));
        if (!M3Widgets.RowCombo(label, ref index, ModifierLabels, supporting) || ModifierHotkey.ValidModifiers[index] == current)
        {
            return false;
        }

        setter(ModifierHotkey.ValidModifiers[index]);
        Save();
        return true;
    }

    public static bool Color(string label, string? supporting, uint current, uint defaultColor, Action<uint> setter)
    {
        var value = ImGui.ColorConvertU32ToFloat4(current);
        if (!M3Widgets.RowColor(label, ref value, ImGui.ColorConvertU32ToFloat4(defaultColor), supporting))
        {
            return false;
        }

        var packed = ImGui.ColorConvertFloat4ToU32(value);
        if (packed == current)
        {
            return false;
        }

        setter(packed);
        Save();
        return true;
    }

    public static M3Hotkey ToM3(ModifiableHotkey hotkey)
    {
        if (hotkey.Hotkey == VirtualKey.NO_KEY)
        {
            return M3Hotkey.None;
        }

        bool Has(VirtualKey key)
            => hotkey.Modifier1.Modifier == key || hotkey.Modifier2.Modifier == key;

        return new M3Hotkey(hotkey.Hotkey, Has(VirtualKey.CONTROL), Has(VirtualKey.SHIFT), Has(VirtualKey.MENU));
    }

    public static ModifiableHotkey FromM3(M3Hotkey hotkey)
    {
        if (hotkey.IsEmpty)
        {
            return new ModifiableHotkey();
        }

        var modifiers = new List<ModifierHotkey>(3);
        if (hotkey.Ctrl)
        {
            modifiers.Add(ModifierHotkey.Control);
        }

        if (hotkey.Alt)
        {
            modifiers.Add(ModifierHotkey.Alt);
        }

        if (hotkey.Shift)
        {
            modifiers.Add(ModifierHotkey.Shift);
        }

        var result = new ModifiableHotkey(hotkey.Key);
        if (modifiers.Count > 0)
        {
            result.SetModifier1(modifiers[0]);
        }

        if (modifiers.Count > 1)
        {
            result.SetModifier2(modifiers[1]);
        }

        return result;
    }

    private static (string Label, string Id) SplitLabel(string label)
    {
        var hidden = label.IndexOf("##", StringComparison.Ordinal);
        return hidden < 0 ? (label, label) : (label[..hidden], label);
    }
}
