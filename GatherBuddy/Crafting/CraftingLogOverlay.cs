using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Numerics;

namespace GatherBuddy.Crafting;

public static class CraftingLogOverlay
{
    public static void Initialize()
    {
    }

    public static void Dispose()
    {
    }

    public static unsafe void DrawCraftingLogOptions()
    {
        try
        {
            var recipeWindow = Dalamud.GameGui.GetAddonByName("RecipeNote");
            if (recipeWindow == IntPtr.Zero)
                return;

            var addonPtr = (AtkUnitBase*)recipeWindow.Address;
            if (addonPtr == null || !addonPtr->IsVisible)
                return;

            var node = addonPtr->GetNodeById(103);
            if (node == null || !node->IsVisible())
                return;

            var position = GetNodePosition(node);
            var scale = GetNodeScale(node);
            var size = new Vector2(node->Width, node->Height) * scale;

            ImGuiHelpers.ForceNextWindowMainViewport();
            ImGuiHelpers.SetNextWindowPosRelativeMainViewport(new Vector2(position.X + (4f * scale.X) - 40f, position.Y - 16f - (17f * scale.Y)));

            ImGui.PushStyleColor(ImGuiCol.WindowBg, 0);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4f, 3f));

            ImGui.Begin("###CraftingLogAutoPrepare", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize | 
                ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoTitleBar | 
                ImGuiWindowFlags.NoSavedSettings);

            var enableAutoPrepare = GatherBuddy.Config.EnableAutoPrepareOnCraft;
            if (ImGui.Checkbox("Auto-prepare before craft", ref enableAutoPrepare))
            {
                GatherBuddy.Config.EnableAutoPrepareOnCraft = enableAutoPrepare;
                GatherBuddy.Config.Save();
            }

            ImGui.End();
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor();
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[CraftingLogOverlay] Error drawing overlay: {ex.Message}");
        }
    }

    private static unsafe Vector2 GetNodePosition(AtkResNode* node)
    {
        if (node == null)
            return Vector2.Zero;

        var pos = new Vector2(node->X, node->Y);
        var par = node->ParentNode;
        while (par != null)
        {
            pos *= new Vector2(par->ScaleX, par->ScaleY);
            pos += new Vector2(par->X, par->Y);
            par = par->ParentNode;
        }

        return pos;
    }

    private static unsafe Vector2 GetNodeScale(AtkResNode* node)
    {
        if (node == null)
            return Vector2.One;

        var scale = new Vector2(node->ScaleX, node->ScaleY);
        while (node->ParentNode != null)
        {
            node = node->ParentNode;
            scale *= new Vector2(node->ScaleX, node->ScaleY);
        }

        return scale;
    }
}
