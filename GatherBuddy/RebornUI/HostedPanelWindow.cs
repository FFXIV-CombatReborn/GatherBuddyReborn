using Dalamud.Interface.Windowing;
using RebornMaterial;
using System;
using System.Numerics;

namespace GatherBuddy.RebornUI;

public sealed class HostedPanelWindow(string id, string title, Vector2 size, Vector2 minimumSize, bool bodyScrolls = true) : M3PanelWindow($"{title}###{id}", title, size, new WindowSizeConstraints
{
    MinimumSize = minimumSize,
    MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
})
{
    private readonly bool _bodyScrolls = bodyScrolls;
    private object? _owner;
    private Action? _draw;
    private Func<string>? _status;

    protected override bool BodyScrolls
        => _bodyScrolls;

    protected override (string Text, Vector4? Color) Status
        => (_status?.Invoke() ?? string.Empty, null);

    public void Show(object owner, Action draw, Func<string>? status = null)
    {
        _owner = owner;
        _draw = draw;
        _status = status;
        OpenAndRestore();
    }

    public void Hide(object owner)
    {
        if (ReferenceEquals(_owner, owner))
        {
            IsOpen = false;
        }
    }

    public override bool DrawConditions()
        => _draw != null;

    public override void OnClose()
    {
        _owner = null;
        _draw = null;
        _status = null;
        base.OnClose();
    }

    protected override void DrawContents()
        => _draw?.Invoke();
}
