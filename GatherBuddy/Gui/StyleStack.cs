using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;

namespace GatherBuddy.Gui;

internal sealed class StyleStack : IDisposable
{
    private int _count;

    public StyleStack Push(ImGuiStyleVar variable, Vector2 value)
    {
        ImGui.PushStyleVar(variable, value);
        ++_count;
        return this;
    }

    public StyleStack Push(ImGuiStyleVar variable, float value)
    {
        ImGui.PushStyleVar(variable, value);
        ++_count;
        return this;
    }

    public void Pop(int count = 1)
    {
        count = Math.Min(count, _count);
        if (count <= 0)
        {
            return;
        }

        ImGui.PopStyleVar(count);
        _count -= count;
    }

    public void Dispose()
        => Pop(_count);
}
