using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using RebornMaterial;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace GatherBuddy.RebornUI;

internal static class Ui
{
    public static void ClippedDraw<T>(IReadOnlyList<T> items, Action<T> draw, float rowHeight)
    {
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(items.Count, rowHeight);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; ++i)
            {
                draw(items[i]);
            }
        }

        clipper.End();
        clipper.Destroy();
    }

    public static bool IconListRow(string id, uint iconId, string label, bool selected, float iconSize, string? trailing = null,
        Vector4? trailingColor = null)
    {
        var s = M3.Scheme;
        var height = iconSize + M3.Space2;
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var min = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var max = min + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();
        if (selected)
        {
            drawList.AddRectFilled(min, max, M3.U32(s.SecondaryContainer), M3.ShapeSmall);
        }
        else if (hovered)
        {
            drawList.AddRectFilled(min, max, M3.U32(s.OnSurface, M3.StateHover), M3.ShapeSmall);
        }

        var iconMin = min + new Vector2(M3.Space1, (height - iconSize) * 0.5f);
        if (iconId > 0)
        {
            M3ActionIcon.Image(drawList, M3ActionIcon.GameIcon(iconId), iconMin, iconMin + new Vector2(iconSize), M3.ShapeExtraSmall);
        }

        var textY = min.Y + (height - ImGui.GetTextLineHeight()) * 0.5f;
        var trailingSize = string.IsNullOrEmpty(trailing) ? 0f : ImGui.CalcTextSize(trailing).X;
        if (trailingSize > 0f)
        {
            drawList.AddText(new Vector2(max.X - M3.Space2 - trailingSize, textY), M3.U32(trailingColor ?? s.OnSurfaceVariant), trailing);
        }

        var textX = iconMin.X + iconSize + M3.Space2;
        var shown = M3Navigation.Truncate(label, MathF.Max(8f, max.X - textX - M3.Space2 - (trailingSize > 0f ? trailingSize + M3.Space2 : 0f)));
        drawList.AddText(new Vector2(textX, textY), M3.U32(selected ? s.OnSecondaryContainer : s.OnSurface), shown);
        return clicked;
    }

    public static bool IsDropping(string label)
        => !ImGui.AcceptDragDropPayload(label).IsNull;

    public static string ReadClipboard()
    {
        try
        {
            return ImGui.GetClipboardText() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static void TextShadowed(ImDrawListPtr drawList, Vector2 position, string text, uint color, uint shadowColor, int shadowWidth = 1)
    {
        for (var i = -shadowWidth; i <= shadowWidth; i++)
        {
            for (var j = -shadowWidth; j <= shadowWidth; j++)
            {
                if (i != 0 || j != 0)
                {
                    drawList.AddText(position + new Vector2(i, j), shadowColor, text);
                }
            }
        }

        drawList.AddText(position, color, text);
    }

    public static void RightAligned(string text, float offset = 0f)
    {
        var gap = ImGui.GetContentRegionAvail().X - offset - ImGui.CalcTextSize(text).X;
        if (gap > 0f)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + gap);
        }

        ImGui.TextUnformatted(text);
    }

    public static void Centered(string text)
    {
        var gap = (ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(text).X) * 0.5f;
        if (gap > 0f)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + gap);
        }

        ImGui.TextUnformatted(text);
    }

    public static void Text(string text, Vector4 color)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
    }

    public static void Text(string text, uint color)
    {
        using var c = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
    }

    public static void Framed(string text, Vector4 fill, Vector2 size = default, Vector4? textColor = null)
    {
        var padding = ImGui.GetStyle().FramePadding;
        var textSize = ImGui.CalcTextSize(text);
        if (size.X <= 0f)
        {
            size.X = textSize.X + 2 * padding.X;
        }

        if (size.Y <= 0f)
        {
            size.Y = textSize.Y + 2 * padding.Y;
        }

        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var drawList = ImGui.GetWindowDrawList();
        var filled = fill.W > 0f;
        if (filled)
        {
            drawList.AddRectFilled(min, max, M3.U32(fill), MathF.Min(M3.ShapeSmall, size.Y * 0.5f));
        }

        var color = textColor ?? (filled ? M3.ContentOn(fill) : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        drawList.PushClipRect(min, max, true);
        drawList.AddText(min + (size - textSize) * 0.5f, M3.U32(color), text);
        drawList.PopClipRect();
        ImGui.Dummy(size);
    }

    public static void Framed(string text, uint fill, Vector2 size = default, uint textColor = 0)
        => Framed(text, ImGui.ColorConvertU32ToFloat4(fill), size, textColor == 0 ? null : ImGui.ColorConvertU32ToFloat4(textColor));

    private static uint _editId;
    private static string _editText = string.Empty;

    public static bool CommitField(string id, string hint, string current, out string committed, float width, int maxLength = 256)
    {
        var key = ImGui.GetID(id);
        var text = _editId == key ? _editText : current;
        M3TextField.Draw(id, hint, ref text, width, maxLength);
        committed = text;
        if (ImGui.IsItemActive())
        {
            _editId = key;
            _editText = text;
            return false;
        }

        if (_editId != key)
        {
            return false;
        }

        _editId = 0;
        _editText = string.Empty;
        return text != current;
    }

    public static void CenteredIcon(FontAwesomeIcon icon, Vector4 color, string? tooltip = null)
    {
        var size = M3Draw.MeasureIcon(icon);
        var line = ImGui.GetTextLineHeight();
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, line));
        M3Draw.Icon(ImGui.GetWindowDrawList(), icon, start + new Vector2((width - size.X) * 0.5f, (line - size.Y) * 0.5f), color);
        if (tooltip != null)
        {
            M3Tooltip.Hovered(tooltip);
        }
    }

    public static void HoverIcon(ISharedImmediateTexture icon, Vector2 size)
    {
        if (icon.TryGetWrap(out var wrap, out _))
        {
            HoverIcon(wrap, size);
        }
        else
        {
            ImGui.Dummy(size);
        }
    }

    public static void HoverIcon(IDalamudTextureWrap icon, Vector2 size)
    {
        ImGui.Image(icon.Handle, size);
        HoverIconTooltip(icon, size);
    }

    public static void HoverIconTooltip(IDalamudTextureWrap icon, Vector2 drawnSize, string? text = null)
    {
        var full = new Vector2(icon.Width, icon.Height);
        if (drawnSize.X >= full.X && drawnSize.Y >= full.Y && string.IsNullOrEmpty(text))
        {
            return;
        }

        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            return;
        }

        using var tooltip = ImRaii.Tooltip();
        if (drawnSize.X < full.X || drawnSize.Y < full.Y)
        {
            ImGui.Image(icon.Handle, full);
        }

        if (!string.IsNullOrEmpty(text))
        {
            ImGui.TextUnformatted(text);
        }
    }
}
