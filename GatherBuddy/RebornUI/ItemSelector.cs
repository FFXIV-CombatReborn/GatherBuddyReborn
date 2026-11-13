using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.RebornUI;

public class ItemSelector<T>
{
    [Flags]
    public enum Flags : byte
    {
        None = 0x00,
        Delete = 0x01,
        Add = 0x02,
        Import = 0x04,
        Duplicate = 0x08,

        Filter = 0x10,
        Move = 0x20,
        Drop = 0x40,

        ButtonMask = 0x0F,
        All = 0x7F,
    }

    private enum NameAction
    {
        None,
        Add,
        Import,
        Duplicate,
    }

    protected readonly IList<T> Items;
    private readonly Flags _flags;

    protected readonly List<int> FilteredItems;
    protected string Filter = string.Empty;
    protected bool FilterDirty = true;
    private int _lastSize;

    private NameAction _nameAction;
    private string _newName = string.Empty;
    private object? _dragDropData;

    private readonly string _label = "##ItemSelector";

    public string Label
    {
        get => _label;
        init
        {
            _label = value;
            DragDropLabel = $"{value}DragDrop";
            MoveLabel = $"{value}Move";
        }
    }

    public readonly string DragDropLabel = "##ItemSelectorDragDrop";
    public readonly string MoveLabel = "##ItemSelectorMove";

    public ItemSelector(IList<T> items, Flags flags = Flags.None)
    {
        Items = items;
        _lastSize = Items.Count;
        _flags = flags;
        FilteredItems = new List<int>(Items.Count);
        for (var i = 0; i < Items.Count; ++i)
        {
            FilteredItems.Add(i);
        }

        Label = "##ItemSelector";
        SetCurrent(0);
    }

    public unsafe void CreateDropSource<TData>(TData data, string tooltip)
    {
        using var source = ImRaii.DragDropSource();
        if (!source)
        {
            return;
        }

        _dragDropData = data;
        ImGui.SetDragDropPayload(DragDropLabel, null, 0);
        ImGui.TextUnformatted(tooltip);
    }

    public bool CreateDropTarget<TData>(Action<TData> action)
    {
        using var target = ImRaii.DragDropTarget();
        if (!target || !Ui.IsDropping(DragDropLabel) || _dragDropData is not TData data)
        {
            return false;
        }

        action(data);
        return true;
    }

    public bool CreateDropTarget<TData>(Func<TData, bool> func)
    {
        using var target = ImRaii.DragDropTarget();
        if (!target || !Ui.IsDropping(DragDropLabel))
        {
            return false;
        }

        return _dragDropData is TData data && func(data);
    }

    public void SetFilterDirty()
        => FilterDirty = true;

    public int CurrentIdx = 0;
    public T? Current { get; private set; }

    protected void ClearCurrentSelection()
    {
        CurrentIdx = -1;
        Current = default;
    }

    public void TryRestoreCurrent()
    {
        CurrentIdx = Current == null ? -1 : Items.IndexOf(Current);
        if (CurrentIdx == -1)
        {
            Current = default;
        }
    }

    private void SetCurrent(int idx)
    {
        if (idx >= 0 && idx < Items.Count)
        {
            CurrentIdx = idx;
            Current = Items[idx];
        }
        else if (Items.Count > 0)
        {
            CurrentIdx = Items.Count - 1;
            Current = Items[^1];
        }
        else
        {
            ClearCurrentSelection();
        }
    }

    protected void SetCurrent(T item)
    {
        var idx = Items.IndexOf(item);
        if (idx >= 0)
        {
            SetCurrent(idx);
        }
    }

    public T? EnsureCurrent()
    {
        TryRestoreCurrent();
        if (Current == null)
        {
            SetCurrent(0);
        }

        return Current;
    }

    protected virtual bool OnDraw(int idx)
        => throw new NotImplementedException();

    protected virtual bool Filtered(int idx)
        => throw new NotImplementedException();

    protected virtual bool OnDelete(int idx)
        => throw new NotImplementedException();

    protected virtual bool OnAdd(string name)
        => throw new NotImplementedException();

    protected virtual bool OnMove(int idx1, int idx2)
        => throw new NotImplementedException();

    protected virtual bool OnClipboardImport(string name, string data)
        => throw new NotImplementedException();

    protected virtual bool OnDuplicate(string name, int idx)
        => throw new NotImplementedException();

    protected virtual void OnDrop(object? data, int idx)
        => throw new NotImplementedException();

    protected virtual bool DeleteButtonEnabled()
        => ImGui.GetIO().KeyCtrl;

    protected virtual string DeleteButtonTooltip()
        => "Delete the selected entry. Hold Control while clicking.";

    protected static bool ListRow(string label, bool selected, bool dimmed = false)
    {
        var s = M3.Scheme;
        var height = M3.FitText(32f, 6f);
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var min = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##row", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var max = min + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();
        if (selected)
        {
            drawList.AddRectFilled(min, max, M3.U32(M3.StateLayer(s.SecondaryContainer, s.OnSecondaryContainer, hovered, active)), height * 0.5f);
        }
        else if (hovered)
        {
            drawList.AddRectFilled(min, max, M3.U32(s.OnSurface, active ? M3.StateHover + M3.StatePressed : M3.StateHover), height * 0.5f);
        }

        var padding = 12f * M3.Scale;
        var shown = M3Navigation.Truncate(label, width - 2 * padding);
        var color = selected ? s.OnSecondaryContainer : dimmed ? M3Text.ColorOf(M3TextStyle.Faint) : s.OnSurface;
        drawList.AddText(new Vector2(min.X + padding, min.Y + (height - ImGui.GetTextLineHeight()) * 0.5f), M3.U32(color), shown);
        if (shown != label)
        {
            M3Tooltip.Hovered(label);
        }

        return clicked;
    }

    private void InternalDraw(int idx)
    {
        using var id = ImRaii.PushId(idx);
        if (OnDraw(idx) && idx != CurrentIdx)
        {
            CurrentIdx = idx;
            Current = Items[idx];
        }

        if (_flags.HasFlag(Flags.Move))
        {
            using var source = ImRaii.DragDropSource();
            if (source)
            {
                _dragDropData = idx;
                unsafe
                {
                    ImGui.SetDragDropPayload(MoveLabel, null, 0);
                }

                ImGui.TextUnformatted($"Moving entry {idx + 1}");
            }
        }

        if ((_flags & (Flags.Move | Flags.Drop)) == Flags.None)
        {
            return;
        }

        using var target = ImRaii.DragDropTarget();
        if (!target)
        {
            return;
        }

        if (Ui.IsDropping(DragDropLabel))
        {
            OnDrop(_dragDropData, idx);
        }
        else if (Ui.IsDropping(MoveLabel))
        {
            var oldIdx = _dragDropData is int dragged ? dragged : idx;
            if (OnMove(oldIdx, idx) && oldIdx == CurrentIdx)
            {
                SetCurrent(idx);
            }
        }
    }

    private void UpdateFilteredItems()
    {
        if (_lastSize != Items.Count)
        {
            FilterDirty = true;
            _lastSize = Items.Count;
        }

        if (!FilterDirty)
        {
            return;
        }

        FilteredItems.Clear();
        for (var idx = 0; idx < Items.Count; ++idx)
        {
            if (!Filtered(idx))
            {
                FilteredItems.Add(idx);
            }
        }

        FilterDirty = false;
    }

    private void DrawFilter(float width)
    {
        if (!_flags.HasFlag(Flags.Filter))
        {
            return;
        }

        var filter = Filter;
        if (M3Widgets.SearchField("##filter", "Search", ref filter, width) && filter != Filter)
        {
            Filter = filter;
            FilterDirty = true;
        }

        if (Filter.Length > 0 && ImGui.IsKeyPressed(ImGuiKey.Enter) && ImGui.IsWindowFocused())
        {
            UpdateFilteredItems();
            if (FilteredItems.Count > 0)
            {
                SetCurrent(FilteredItems[0]);
            }
        }

        ImGui.Dummy(new Vector2(0, M3.Space1));
    }

    private bool HasButtons
        => (_flags & Flags.ButtonMask) != 0;

    private void DrawButtons()
    {
        var size = M3Widgets.IconButtonSize;
        var first = true;

        void Next()
        {
            if (!first)
            {
                ImGui.SameLine(0, M3.Space1);
            }

            first = false;
        }

        if (_flags.HasFlag(Flags.Add))
        {
            Next();
            if (M3Widgets.IconButton("##add", FontAwesomeIcon.Plus, "Add a new entry", M3ButtonStyle.Tonal, diameter: size))
            {
                OpenNameDialog(NameAction.Add);
            }
        }

        if (_flags.HasFlag(Flags.Import))
        {
            Next();
            if (M3Widgets.IconButton("##import", FontAwesomeIcon.Paste, "Import an entry from your clipboard", diameter: size))
            {
                OpenNameDialog(NameAction.Import);
            }
        }

        if (_flags.HasFlag(Flags.Duplicate))
        {
            Next();
            if (M3Widgets.IconButton("##duplicate", FontAwesomeIcon.Clone, "Duplicate the selected entry", diameter: size,
                    enabled: CurrentIdx >= 0))
            {
                OpenNameDialog(NameAction.Duplicate);
            }
        }

        if (_flags.HasFlag(Flags.Delete))
        {
            Next();
            if (M3Widgets.IconButton("##delete", FontAwesomeIcon.Trash, DeleteButtonTooltip(), diameter: size,
                    tint: DeleteButtonEnabled() ? M3.Scheme.Error : null, enabled: CurrentIdx >= 0 && DeleteButtonEnabled())
             && OnDelete(CurrentIdx))
            {
                FilterDirty = true;
                SetCurrent(CurrentIdx > 0 ? CurrentIdx - 1 : CurrentIdx);
            }
        }
    }

    private void OpenNameDialog(NameAction action)
    {
        _nameAction = action;
        _newName = action == NameAction.Duplicate && Current != null ? $"{NameOf(Current)} (copy)" : string.Empty;
        M3Dialog.Open("##name");
    }

    protected virtual string NameOf(T item)
        => item?.ToString() ?? string.Empty;

    private void DrawNameDialog()
    {
        var (headline, icon) = _nameAction switch
        {
            NameAction.Import => ("Import from clipboard", FontAwesomeIcon.Paste),
            NameAction.Duplicate => ("Duplicate entry", FontAwesomeIcon.Clone),
            _ => ("New entry", FontAwesomeIcon.Plus),
        };

        using var dialog = M3Dialog.Begin("##name", headline, "Give it a name.", icon);
        if (!dialog.IsOpen)
        {
            return;
        }

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        M3Widgets.TextField("##newName", "Name", ref _newName, ImGui.GetContentRegionAvail().X, 128);
        var submit = ImGui.IsKeyPressed(ImGuiKey.Enter) && _newName.Trim().Length > 0;
        var pressed = M3Dialog.Actions(
        [
            new M3DialogAction("Cancel"),
            new M3DialogAction(_nameAction == NameAction.Import ? "Import" : "Create", M3ButtonStyle.Filled, icon, _newName.Trim().Length > 0),
        ]);
        if (pressed != 1 && !submit)
        {
            return;
        }

        if (submit)
        {
            ImGui.CloseCurrentPopup();
        }

        var name = _newName.Trim();
        var done = _nameAction switch
        {
            NameAction.Add => OnAdd(name),
            NameAction.Import => OnClipboardImport(name, Ui.ReadClipboard()),
            NameAction.Duplicate => CurrentIdx >= 0 && OnDuplicate(name, CurrentIdx),
            _ => false,
        };

        if (done)
        {
            TryRestoreCurrent();
            SetFilterDirty();
        }
        else if (_nameAction == NameAction.Import)
        {
            M3Snackbar.Show("The clipboard does not hold a valid entry.", severity: M3Severity.Error);
        }

        _nameAction = NameAction.None;
        _newName = string.Empty;
    }

    public void Draw(float width)
    {
        using var id = ImRaii.PushId(Label);
        using var group = ImRaii.Group();
        var buttonRow = HasButtons ? M3Widgets.IconButtonSize + M3.Space1 : 0f;
        using (var panel = M3Panel.Begin("##list", new Vector2(width, MathF.Max(1f, ImGui.GetContentRegionAvail().Y - buttonRow)), false))
        {
            if (panel)
            {
                DrawFilter(ImGui.GetContentRegionAvail().X);
                UpdateFilteredItems();
                using var rows = ImRaii.Child("##rows", new Vector2(-1, -1));
                if (rows)
                {
                    var clipper = ImGui.ImGuiListClipper();
                    clipper.Begin(FilteredItems.Count);
                    while (clipper.Step())
                    {
                        for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; ++i)
                        {
                            InternalDraw(FilteredItems[i]);
                        }
                    }

                    clipper.End();
                    clipper.Destroy();
                }
            }
        }

        if (HasButtons)
        {
            ImGui.Dummy(new Vector2(0, M3.Space1 * 0.5f));
            DrawButtons();
        }

        DrawNameDialog();
    }
}

public static class ItemDetailsWindow
{
    public static void Draw(string label, Action drawHeader, Action drawDetails)
    {
        using var id = ImRaii.PushId(label);
        using var panel = M3Panel.Begin("##details", ImGui.GetContentRegionAvail(), false);
        if (!panel)
        {
            return;
        }

        using (ImRaii.Group())
        {
            drawHeader();
        }

        M3Widgets.Divider(4f);
        using var content = ImRaii.Child("##content", new Vector2(-1, -1), false, ImGuiWindowFlags.HorizontalScrollbar);
        if (content)
        {
            drawDetails();
        }
    }
}
