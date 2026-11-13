using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using GatherBuddy.Utility;
using RebornMaterial;
using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.RebornUI.Tables;

public static class Table
{
    public const float ArrowWidth = 10;
}

public class Table<T>
{
    protected bool FilterDirty = true;
    protected bool SortDirty = true;
    protected readonly IReadOnlyCollection<T> Items;
    internal readonly List<(T, int)> FilteredItems;

    protected readonly string Label;
    protected readonly Column<T>[] Headers;

    protected float ItemHeight { get; set; }
    public float ExtraHeight { get; set; }

    protected bool Sortable
    {
        get => Flags.HasFlag(ImGuiTableFlags.Sortable);
        set => Flags = value ? Flags | ImGuiTableFlags.Sortable : Flags & ~ImGuiTableFlags.Sortable;
    }

    protected int SortIdx = -1;

    public ImGuiTableFlags Flags = ImGuiTableFlags.RowBg
      | ImGuiTableFlags.Sortable
      | ImGuiTableFlags.BordersOuterH
      | ImGuiTableFlags.ScrollY
      | ImGuiTableFlags.ScrollX
      | ImGuiTableFlags.PreciseWidths
      | ImGuiTableFlags.SizingFixedFit
      | ImGuiTableFlags.BordersInnerV
      | ImGuiTableFlags.NoBordersInBodyUntilResize;

    public int TotalItems
        => Items.Count;

    public int CurrentItems
        => FilteredItems.Count;

    public int TotalColumns
        => Headers.Length;

    public int VisibleColumns { get; private set; }

    public IReadOnlyList<(T Item, int Index)> GetFilteredItems()
        => FilteredItems;

    public Table(string label, IReadOnlyCollection<T> items, params Column<T>[] headers)
    {
        Label = label;
        Items = items;
        Headers = headers;
        FilteredItems = [with(Items.Count)];
        VisibleColumns = Headers.Length;
    }

    public void Draw(float itemHeight)
    {
        ItemHeight = itemHeight;
        using var id = ImRaii.PushId(Label);
        UpdateFilter();
        DrawTableInternal();
    }

    public bool WouldBeVisible(T value)
    {
        foreach (var header in Headers)
        {
            if (!header.FilterFunc(value))
            {
                return false;
            }
        }

        return true;
    }

    protected virtual void PreDraw()
    { }

    private void SortInternal()
    {
        if (!Sortable)
        {
            return;
        }

        var sortSpecs = ImGui.TableGetSortSpecs();
        SortDirty |= sortSpecs.SpecsDirty;
        if (!SortDirty)
        {
            return;
        }

        SortIdx = sortSpecs.Specs.ColumnIndex;
        if (Headers.Length <= SortIdx)
        {
            SortIdx = 0;
        }

        var header = Headers[SortIdx];
        switch (sortSpecs.Specs.SortDirection)
        {
            case ImGuiSortDirection.Ascending:
                header.PreSort();
                FilteredItems.StableSort((a, b) => header.Compare(a.Item1, b.Item1));
                header.PostSort();
                break;
            case ImGuiSortDirection.Descending:
                header.PreSort();
                FilteredItems.StableSort((a, b) => header.CompareInv(a.Item1, b.Item1));
                header.PostSort();
                break;
            default:
                SortIdx = -1;
                break;
        }

        SortDirty = false;
        sortSpecs.SpecsDirty = false;
    }

    private void UpdateFilter()
    {
        if (!FilterDirty)
        {
            return;
        }

        FilteredItems.Clear();
        var idx = 0;
        foreach (var item in Items)
        {
            if (WouldBeVisible(item))
            {
                FilteredItems.Add((item, idx));
            }

            idx++;
        }

        FilterDirty = false;
        SortDirty = true;
    }

    private void DrawItem((T Item, int Index) pair)
    {
        using var id = ImRaii.PushId(pair.Index);
        for (var column = 0; column < Headers.Length; ++column)
        {
            using var columnId = ImRaii.PushId(column);
            if (ImGui.TableNextColumn())
            {
                Headers[column].DrawColumn(pair.Item, pair.Index);
            }
        }
    }

    private void DrawTableInternal()
    {
        var s = M3.Scheme;
        var size = ImGui.GetContentRegionAvail() - ExtraHeight * Vector2.UnitY * ImGuiHelpers.GlobalScale;

        using var style = M3Table.PushStyle();
        using var colors = ImRaii.PushColor(ImGuiCol.TableRowBg, Vector4.Zero)
            .Push(ImGuiCol.TableRowBgAlt, M3.Alpha(s.SurfaceContainerHigh, 0.45f))
            .Push(ImGuiCol.HeaderHovered, M3.Alpha(s.OnSurface, M3.StateHover))
            .Push(ImGuiCol.HeaderActive, M3.Alpha(s.OnSurface, M3.StatePressed));
        using var table = ImRaii.Table("Table", Headers.Length, Flags, size);
        if (!table)
        {
            return;
        }

        PreDraw();
        ImGui.TableSetupScrollFreeze(1, 1);
        foreach (var header in Headers)
        {
            ImGui.TableSetupColumn(header.Label, header.Flags, header.Width);
        }

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers, TableFilter.Height + M3.Space1);
        VisibleColumns = 0;
        for (var i = 0; i < Headers.Length; ++i)
        {
            using var id = ImRaii.PushId(i);
            if (ImGui.TableGetColumnFlags(i).HasFlag(ImGuiTableColumnFlags.IsEnabled))
            {
                ++VisibleColumns;
            }

            if (!ImGui.TableSetColumnIndex(i))
            {
                continue;
            }

            using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
            {
                ImGui.TableHeader(string.Empty);
                ImGui.SameLine();
            }

            if (Headers[i].DrawFilter())
            {
                FilterDirty = true;
            }
        }

        SortInternal();

        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(FilteredItems.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; ++i)
            {
                DrawItem(FilteredItems[i]);
            }
        }

        clipper.End();
        clipper.Destroy();
    }
}
