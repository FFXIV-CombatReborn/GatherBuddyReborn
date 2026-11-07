using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GatherBuddy.Gui;

public static class ImGuiEx
{
    public readonly record struct RequiredPluginInfo
    {
        public readonly string InternalName;
        public readonly string? VanityName;
        public readonly Version? MinVersion;

        public RequiredPluginInfo(string internalName)
        {
            InternalName = internalName;
            VanityName = null;
            MinVersion = null;
        }

        public RequiredPluginInfo(string internalName, string vanityName)
        {
            InternalName = internalName;
            VanityName = vanityName;
            MinVersion = null;
        }

        public RequiredPluginInfo(string internalName, Version minVersion)
        {
            InternalName = internalName;
            VanityName = null;
            MinVersion = minVersion;
        }

        public RequiredPluginInfo(string internalName, string vanityName, Version minVersion)
        {
            InternalName = internalName;
            VanityName = vanityName;
            MinVersion = minVersion;
        }
    }

    public static void PluginAvailabilityIndicator(IEnumerable<RequiredPluginInfo> pluginInfos, string? prependText = null, bool all = true)
    {
        prependText ??= all
            ? "The following plugins are required to be installed and enabled:"
            : "One of the following plugins is required to be installed and enabled";

        bool pass;
        if (all)
        {
            pass = pluginInfos.All(info => Dalamud.PluginInterface.InstalledPlugins.Any(x =>
                x.IsLoaded && x.InternalName == info.InternalName && (info.MinVersion == null || x.Version >= info.MinVersion)));
        }
        else
        {
            pass = pluginInfos.Any(info => Dalamud.PluginInterface.InstalledPlugins.Any(x =>
                x.IsLoaded && x.InternalName == info.InternalName && (info.MinVersion == null || x.Version >= info.MinVersion)));
        }

        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(pass ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed,
            pass ? FontAwesomeIcon.Check.ToIconString() : "\uf00d");
        ImGui.PopFont();

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
            ImGui.TextUnformatted(prependText);
            ImGui.PopTextWrapPos();

            foreach (var info in pluginInfos)
            {
                var plugin = Dalamud.PluginInterface.InstalledPlugins.FirstOrDefault(x =>
                    x.IsLoaded && x.InternalName == info.InternalName);

                if (plugin != null)
                {
                    if (info.MinVersion == null || plugin.Version >= info.MinVersion)
                    {
                        ImGui.TextColored(ImGuiColors.ParsedGreen,
                            $"- {info.VanityName ?? info.InternalName}" + (info.MinVersion == null ? "" : $" {info.MinVersion}+"));
                    }
                    else
                    {
                        ImGui.TextColored(ImGuiColors.ParsedGreen, $"- {info.VanityName ?? info.InternalName} ");
                        ImGui.SameLine(0, 0);
                        ImGui.TextColored(ImGuiColors.DalamudRed, $"{info.MinVersion}+ ");
                        ImGui.SameLine(0, 0);
                        ImGui.TextUnformatted("(outdated)");
                    }
                }
                else
                {
                    ImGui.TextColored(ImGuiColors.DalamudRed,
                        $"- {info.VanityName ?? info.InternalName} " + (info.MinVersion == null ? "" : $"{info.MinVersion}+ "));
                    ImGui.SameLine(0, 0);
                    ImGui.TextUnformatted("(not installed)");
                }
            }
            ImGui.EndTooltip();
        }
    }

    public static void InfoMarker(string helpText, Vector4? color = null, string? symbolOverride = null, bool sameLine = true, bool preserveCursor = false)
    {
        if (preserveCursor && sameLine)
        {
            ImGui.SameLine(0, 0);
        }
        else if (sameLine)
        {
            ImGui.SameLine();
        }

        var cursor = ImGui.GetCursorPos();
        ImGui.PushFont(UiBuilder.IconFont);

        if (preserveCursor)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() -
                ImGui.CalcTextSize(symbolOverride ?? FontAwesomeIcon.InfoCircle.ToIconString()).X);
        }

        ImGui.TextColored(color ?? ImGuiColors.DalamudGrey3,
            symbolOverride ?? FontAwesomeIcon.InfoCircle.ToIconString());
        ImGui.PopFont();

        if (preserveCursor)
        {
            ImGui.SetCursorPos(cursor);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
            ImGui.TextUnformatted(helpText);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    public static bool HoveredAndClicked(string? tooltip = null, ImGuiMouseButton btn = ImGuiMouseButton.Left, bool requireCtrl = false)
    {
        if (ImGui.IsItemHovered() && ImGui.GetMouseDragDelta().X < 2f && ImGui.GetMouseDragDelta().Y < 2f)
        {
            if (tooltip != null)
            {
                ImGui.SetTooltip(tooltip);
            }
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            return (!requireCtrl || ImGui.GetIO().KeyCtrl) && ImGui.IsMouseReleased(btn);
        }
        return false;
    }
}
