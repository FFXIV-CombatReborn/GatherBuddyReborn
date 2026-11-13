using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;

namespace GatherBuddy.RebornUI.Tables;

public class Column<TItem>
{
    public string Label = string.Empty;
    public ImGuiTableColumnFlags Flags = ImGuiTableColumnFlags.NoResize;

    public virtual float Width
        => -1f;

    public string FilterLabel
        => $"##{Label}Filter";

    public virtual bool DrawFilter()
    {
        TableFilter.HeaderLabel(Label);
        return false;
    }

    public virtual bool FilterFunc(TItem item)
        => true;

    public virtual int Compare(TItem lhs, TItem rhs)
        => 0;

    public virtual void DrawColumn(TItem item, int idx)
    { }

    public virtual void PreSort()
    { }

    public virtual void PostSort()
    { }

    public int CompareInv(TItem lhs, TItem rhs)
        => Compare(rhs, lhs);
}

public class ColumnString<TItem> : Column<TItem>
{
    public ColumnString()
        => Flags &= ~ImGuiTableColumnFlags.NoResize;

    public string FilterValue = string.Empty;
    protected Regex? FilterRegex;

    public virtual string ToName(TItem item)
        => item!.ToString() ?? string.Empty;

    public override int Compare(TItem lhs, TItem rhs)
        => string.Compare(ToName(lhs), ToName(rhs), StringComparison.InvariantCulture);

    public override bool DrawFilter()
    {
        var value = FilterValue;
        if (!TableFilter.Text(FilterLabel, Label, ref value, TableFilter.CellWidth) || value == FilterValue)
        {
            return false;
        }

        FilterValue = value;
        try
        {
            FilterRegex = new Regex(FilterValue, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch
        {
            FilterRegex = null;
        }

        return true;
    }

    public override bool FilterFunc(TItem item)
    {
        if (FilterValue.Length == 0)
        {
            return true;
        }

        var name = ToName(item);
        return FilterRegex?.IsMatch(name) ?? name.Contains(FilterValue, StringComparison.OrdinalIgnoreCase);
    }

    public override void DrawColumn(TItem item, int _)
        => ImGui.TextUnformatted(ToName(item));
}

public class ColumnNumber<TItem>(ColumnNumber<TItem>.ComparisonMethod method) : ColumnString<TItem>
{
    public enum ComparisonMethod : byte
    {
        Equal = 0,
        LessEqual = 1,
        GreaterEqual = 2,
    }

    protected readonly ComparisonMethod Comparison = method;
    protected int? FilterNumber;

    public virtual int ToValue(TItem item)
        => item!.GetHashCode();

    public override int Compare(TItem lhs, TItem rhs)
        => ToValue(lhs).CompareTo(ToValue(rhs));

    public override string ToName(TItem item)
        => ToValue(item).ToString();

    public override bool DrawFilter()
    {
        if (!base.DrawFilter())
        {
            return false;
        }

        if (int.TryParse(FilterValue, out var number))
        {
            FilterNumber = number;
            FilterRegex = null;
        }
        else
        {
            FilterNumber = null;
        }

        return true;
    }

    public override bool FilterFunc(TItem item)
    {
        if (!FilterNumber.HasValue)
        {
            return base.FilterFunc(item);
        }

        var value = ToValue(item);
        return Comparison switch
        {
            ComparisonMethod.Equal => value == FilterNumber.Value,
            ComparisonMethod.LessEqual => value <= FilterNumber.Value,
            ComparisonMethod.GreaterEqual => value >= FilterNumber.Value,
            _ => true,
        };
    }

    public override void DrawColumn(TItem item, int _)
        => Ui.RightAligned(ToName(item));
}

public class ColumnFlags<T, TItem> : Column<TItem> where T : struct, Enum
{
    private static readonly T[] EnumValues = Enum.GetValues<T>();
    private static readonly string[] EnumNames = Enum.GetNames<T>();

    public T AllFlags = default;
    protected ImGuiComboFlags ComboFlags = ImGuiComboFlags.NoArrowButton;

    protected virtual IReadOnlyList<T> Values
        => EnumValues;

    protected virtual string[] Names
        => EnumNames;

    public virtual T FilterValue
        => default;

    protected virtual void SetValue(T value, bool enable)
    { }

    protected virtual bool DrawCheckbox(int idx, out bool ret)
    {
        ret = FilterValue.HasFlag(Values[idx]);
        return TableFilter.MenuCheckbox(Names[idx], ref ret);
    }

    public override bool DrawFilter()
    {
        using var id = ImRaii.PushId(FilterLabel);
        var all = FilterValue.HasFlag(AllFlags);
        if (TableFilter.Button("##filter", Label, !all, TableFilter.CellWidth, out var cleared))
        {
            ImGui.OpenPopup("##menu");
        }

        if (cleared)
        {
            SetValue(AllFlags, true);
            return true;
        }

        using var menu = M3Menu.Begin("##menu");
        if (!menu.IsOpen)
        {
            return false;
        }

        var ret = false;
        if (TableFilter.MenuCheckbox("Show all", ref all))
        {
            SetValue(AllFlags, all);
            ret = true;
        }

        M3Menu.Divider();
        var count = Names.Length;
        for (var i = 0; i < count; ++i)
        {
            if (!DrawCheckbox(i, out var value))
            {
                continue;
            }

            SetValue(Values[i], value);
            ret = true;
        }

        return ret;
    }
}

public abstract class TriStateColumnFlags<T, TItem> : Column<TItem> where T : struct, Enum
{
    public T AllFlags = default;
    protected ImGuiComboFlags ComboFlags = ImGuiComboFlags.NoArrowButton;

    protected abstract IReadOnlyList<(T On, T Off)> Values { get; }

    protected abstract string[] Names { get; }

    public virtual T FilterValue
        => default;

    protected virtual void SetValue((T On, T Off) value, bool? enable)
    { }

    protected virtual void SetValue(T flags, bool value)
    { }

    protected virtual bool DrawCheckbox(int idx, out bool? ret)
    {
        var (on, off) = Values[idx];
        bool? current = FilterValue.HasFlag(on)
            ? FilterValue.HasFlag(off)
                ? null
                : true
            : false;

        ret = current;
        return TableFilter.MenuTriState(Names[idx], ref ret);
    }

    public override bool DrawFilter()
    {
        using var id = ImRaii.PushId(FilterLabel);
        var all = FilterValue.HasFlag(AllFlags);
        if (TableFilter.Button("##filter", Label, !all, TableFilter.CellWidth, out var cleared))
        {
            ImGui.OpenPopup("##menu");
        }

        if (cleared)
        {
            SetValue(AllFlags, true);
            return true;
        }

        using var menu = M3Menu.Begin("##menu");
        if (!menu.IsOpen)
        {
            return false;
        }

        var ret = false;
        for (var i = 0; i < Names.Length; ++i)
        {
            if (!DrawCheckbox(i, out var value))
            {
                continue;
            }

            SetValue(Values[i], value);
            ret = true;
        }

        return ret;
    }
}

public static class TableFilter
{
    private static readonly M3Segment[] TriStateSegments =
    [
        new("Any"),
        new("Yes", FontAwesomeIcon.Check),
        new("No", FontAwesomeIcon.Times),
    ];

    public static float Height
        => ImGui.GetFrameHeight();

    public static float CellWidth
        => MathF.Max(1f, ImGui.GetContentRegionAvail().X - Table.ArrowWidth * ImGuiHelpers.GlobalScale);

    public static void HeaderLabel(string label)
    {
        var offset = (Height - ImGui.GetTextLineHeight()) * 0.5f;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offset);
        using (ImRaii.PushFont(M3.LabelSmall))
        using (ImRaii.PushColor(ImGuiCol.Text, M3.Scheme.OnSurfaceVariant))
        {
            ImGui.TextUnformatted(label);
        }
    }

    public static bool Text(string id, string hint, ref string value, float width)
    {
        var s = M3.Scheme;
        var height = Height;
        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, height);
        var active = value.Length > 0;
        var hovered = M3Draw.MouseOver(min, max);
        var drawList = ImGui.GetWindowDrawList();
        var fill = active ? s.SecondaryContainer : hovered ? M3.StateLayer(s.SurfaceContainerHigh, s.OnSurface, true, false) : s.SurfaceContainerHigh;
        drawList.AddRectFilled(min, max, M3.U32(fill), height * 0.5f);

        bool changed;
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero)
            .Push(ImGuiCol.FrameBgHovered, Vector4.Zero)
            .Push(ImGuiCol.FrameBgActive, Vector4.Zero)
            .Push(ImGuiCol.TextDisabled, s.OnSurfaceVariant)
            .Push(ImGuiCol.Text, active ? s.OnSecondaryContainer : s.OnSurface))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(height * 0.45f, ImGui.GetStyle().FramePadding.Y))
            .Push(ImGuiStyleVar.FrameBorderSize, 0f))
        {
            ImGui.SetNextItemWidth(width);
            changed = ImGui.InputTextWithHint(id, hint, ref value, 256);
        }

        if (ImGui.IsItemActive())
        {
            drawList.AddRect(min, max, M3.U32(s.Primary), height * 0.5f, ImDrawFlags.None, MathF.Max(1f, 1.5f * M3.Scale));
        }

        return changed;
    }

    public static bool Button(string id, string label, bool filtered, float width, out bool cleared)
    {
        var s = M3.Scheme;
        var height = Height;
        var min = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        cleared = filtered && ImGui.IsItemClicked(ImGuiMouseButton.Right);
        var max = min + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();
        var fill = filtered ? s.SecondaryContainer : s.SurfaceContainerHigh;
        drawList.AddRectFilled(min, max, M3.U32(M3.StateLayer(fill, s.OnSurface, hovered, ImGui.IsItemActive())), height * 0.5f);

        var content = filtered ? s.OnSecondaryContainer : s.OnSurfaceVariant;
        var padding = height * 0.45f;
        var icon = filtered ? FontAwesomeIcon.Filter : FontAwesomeIcon.ChevronDown;
        var iconSize = M3Draw.MeasureIcon(icon);
        var text = M3Navigation.Truncate(label, MathF.Max(0f, width - 2 * padding - iconSize.X - M3.Space1));
        drawList.AddText(new Vector2(min.X + padding, min.Y + (height - ImGui.GetTextLineHeight()) * 0.5f), M3.U32(content), text);
        M3Draw.Icon(drawList, icon, new Vector2(max.X - padding - iconSize.X, min.Y + (height - iconSize.Y) * 0.5f), content);

        if (filtered)
        {
            M3Tooltip.Hovered("Right-click to clear the filter.");
        }

        return clicked;
    }

    public static bool MenuCheckbox(string label, ref bool value)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + M3.Space2);
        return M3Widgets.LabeledCheckbox(label, ref value);
    }

    public static bool MenuTriState(string label, ref bool? value)
    {
        var index = value switch
        {
            true => 1,
            false => 2,
            _ => 0,
        };

        using var id = ImRaii.PushId(label);
        var start = ImGui.GetCursorPosX() + M3.Space2;
        var width = M3Widgets.SegmentedWidth(TriStateSegments);
        var height = M3Widgets.SegmentedHeight;
        ImGui.SetCursorPosX(start);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (height - ImGui.GetTextLineHeight()) * 0.5f);
        ImGui.TextUnformatted(label);
        ImGui.SameLine(MathF.Max(start + ImGui.CalcTextSize(label).X + M3.Space3, start + 150f * M3.Scale));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - (height - ImGui.GetTextLineHeight()) * 0.5f);
        var picked = M3Widgets.SegmentedButtons("##state", TriStateSegments, index, width);
        if (picked == index)
        {
            return false;
        }

        value = picked switch
        {
            1 => true,
            2 => false,
            _ => null,
        };
        return true;
    }
}
