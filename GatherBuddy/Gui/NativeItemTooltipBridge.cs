using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Functions = GatherBuddy.Plugin.Functions;

namespace GatherBuddy.Gui;

internal sealed unsafe class NativeItemTooltipBridge : IDisposable
{
    private const string HostAddonName = "ChatLog";
    private const string ItemDetailAddonName = "ItemDetail";
    private const string ItemDetailSetPositionPreservingOriginalSignature = "E8 ?? ?? ?? ?? 45 85 ED 4C 8B AC 24";
    private const float AnchorEpsilon = 0.5f;

    private AtkResNode* _anchorNode;
    private nint _anchorHostAddonAddress;
    private delegate* unmanaged[Thiscall]<AtkUnitBase*, short, short, byte, void> _itemDetailSetPositionPreservingOriginal;
    private bool _requestedThisFrame;
    private bool _tooltipVisible;
    private ushort _tooltipParentAddonId;
    private uint _tooltipItemId;
    private Vector2 _tooltipAnchorMin = new(float.NaN);
    private Vector2 _tooltipAnchorMax = new(float.NaN);
    private Vector2 _tooltipNativeAnchorMin = new(float.NaN);
    private Vector2 _tooltipNativeAnchorMax = new(float.NaN);
    private bool _tooltipExpandRight;
    private DateTime _lastHostFailureLog = DateTime.MinValue;
    private DateTime _lastItemDetailRepositionFailureLog = DateTime.MinValue;

    public NativeItemTooltipBridge()
    {
        TryInitializeItemDetailSetPositionFunction();
        Dalamud.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, ItemDetailAddonName, HandleItemDetailLifecycle);
        Dalamud.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, ItemDetailAddonName, HandleItemDetailLifecycle);
        Dalamud.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, ItemDetailAddonName, HandleItemDetailLifecycle);
    }

    public void BeginImGuiFrame()
    {
        _requestedThisFrame = false;
        if (!TryCheckNativeTooltipReadiness(out _))
        {
            HideTooltip();
            return;
        }

        if (_tooltipVisible && !IsCurrentHostAnchorValid())
        {
            GatherBuddy.Log.Debug("[NativeItemTooltipBridge] Host tooltip anchor is no longer valid; hiding native tooltip.");
            HideTooltip();
        }
    }

    public void EndImGuiFrame()
    {
        if (_requestedThisFrame)
        {
            return;
        }

        if (!TryCheckNativeTooltipReadiness(out _))
        {
            HideTooltip();
            return;
        }

        HideTooltip();
    }

    public void RequestItemTooltip(uint itemId, Vector2 rectMin, Vector2 rectMax, bool expandRight)
    {
        if (!TryCheckNativeTooltipReadiness(out _))
        {
            HideTooltip();
            return;
        }

        _requestedThisFrame = true;
        if (itemId == 0)
        {
            HideTooltip();
            return;
        }

        if (!TryPrepareAnchor(out var parentAddonId, out var anchorNode, out var anchorHostAddonAddress))
        {
            HideTooltip();
            return;
        }

        var anchorBoundsChanged = !RectsEqual(_tooltipAnchorMin, rectMin) || !RectsEqual(_tooltipAnchorMax, rectMax);
        var anchorHostChanged = _anchorNode != anchorNode
            || _anchorHostAddonAddress != anchorHostAddonAddress
            || _tooltipParentAddonId != parentAddonId;
        if (_tooltipVisible && _tooltipItemId == itemId && !anchorBoundsChanged && !anchorHostChanged && _tooltipExpandRight == expandRight)
        {
            return;
        }

        if (_tooltipVisible)
        {
            HideTooltip();
        }

        _anchorNode = anchorNode;
        _anchorHostAddonAddress = anchorHostAddonAddress;
        UpdateAnchorBounds(rectMin, rectMax);

        var stage = AtkStage.Instance();
        if (stage == null)
        {
            GatherBuddy.Log.Debug("[NativeItemTooltipBridge] Unable to show tooltip: AtkStage unavailable.");
            ClearTooltipState();
            return;
        }

        var tooltipArgs = default(AtkTooltipManager.AtkTooltipArgs);
        tooltipArgs.ItemArgs.Kind = DetailKind.Item;
        tooltipArgs.ItemArgs.ItemId = unchecked((int)itemId);
        tooltipArgs.ItemArgs.BuyQuantity = -1;
        tooltipArgs.ItemArgs.Flag1 = 0;

        try
        {
            _tooltipExpandRight = expandRight;
            stage->TooltipManager.ShowTooltip(AtkTooltipType.Item, parentAddonId, _anchorNode, &tooltipArgs, &TooltipPositionCallback);
            _tooltipVisible = true;
            _tooltipParentAddonId = parentAddonId;
            _tooltipItemId = itemId;
            _tooltipAnchorMin = rectMin;
            _tooltipAnchorMax = rectMax;
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Warning($"[NativeItemTooltipBridge] Failed to show item tooltip for {itemId}: {ex.Message}");
            ClearTooltipState();
        }
    }

    public void Dispose()
    {
        Dalamud.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, ItemDetailAddonName, HandleItemDetailLifecycle);
        Dalamud.AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, ItemDetailAddonName, HandleItemDetailLifecycle);
        Dalamud.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, ItemDetailAddonName, HandleItemDetailLifecycle);
        HideTooltip();
    }

    private void HandleItemDetailLifecycle(AddonEvent type, AddonArgs args)
    {
        var itemDetailAddon = (AtkUnitBase*)args.Addon.Address;
        if (itemDetailAddon == null)
        {
            return;
        }

        TryRepositionItemDetailAddon(itemDetailAddon);
    }

    private void TryInitializeItemDetailSetPositionFunction()
    {
        try
        {
            var address = Dalamud.SigScanner.ScanText(ItemDetailSetPositionPreservingOriginalSignature);
            _itemDetailSetPositionPreservingOriginal = (delegate* unmanaged[Thiscall]<AtkUnitBase*, short, short, byte, void>)address;
        }
        catch (Exception ex)
        {
            _itemDetailSetPositionPreservingOriginal = null;
            GatherBuddy.Log.Warning($"[NativeItemTooltipBridge] Failed to resolve ItemDetail reposition function: {ex.Message}");
        }
    }

    private void TryRepositionItemDetailAddon(AtkUnitBase* itemDetailAddon)
    {
        if (!_tooltipVisible || _tooltipItemId == 0 || itemDetailAddon->RootNode == null)
        {
            return;
        }

        if (float.IsNaN(_tooltipNativeAnchorMin.X) || float.IsNaN(_tooltipNativeAnchorMin.Y) || float.IsNaN(_tooltipNativeAnchorMax.X) || float.IsNaN(_tooltipNativeAnchorMax.Y))
        {
            return;
        }

        if (!TryGetItemDetailTargetPosition(itemDetailAddon, out var targetX, out var targetY))
        {
            return;
        }

        if (itemDetailAddon->X == targetX && itemDetailAddon->Y == targetY)
        {
            return;
        }

        try
        {
            if (_itemDetailSetPositionPreservingOriginal != null)
            {
                _itemDetailSetPositionPreservingOriginal(itemDetailAddon, targetX, targetY, 1);
            }
            else
            {
                itemDetailAddon->SetPosition(targetX, targetY);
            }
        }
        catch (Exception ex)
        {
            MaybeLogItemDetailRepositionFailure($"Failed to reposition ItemDetail addon: {ex.Message}");
        }
    }

    private bool TryGetItemDetailTargetPosition(AtkUnitBase* itemDetailAddon, out short targetX, out short targetY)
    {
        targetX = 0;
        targetY = 0;

        var tooltipWidth = itemDetailAddon->GetScaledWidth(true);
        var tooltipHeight = itemDetailAddon->GetScaledHeight(true);
        if (tooltipWidth <= 0f || tooltipHeight <= 0f)
        {
            MaybeLogItemDetailRepositionFailure("ItemDetail addon size is unavailable for native tooltip repositioning.");
            return false;
        }

        var nativeDisplaySize = ToNativeUi(ImGui.GetIO().DisplaySize);
        if (nativeDisplaySize.X <= 0f || nativeDisplaySize.Y <= 0f)
        {
            MaybeLogItemDetailRepositionFailure("Native display size is unavailable for ItemDetail repositioning.");
            return false;
        }

        var nativePadding = ToNativeUi(new Vector2(4f, 4f));
        if (nativePadding.X <= 0f)
        {
            nativePadding.X = 1f;
        }

        if (nativePadding.Y <= 0f)
        {
            nativePadding.Y = 1f;
        }

        var desiredX = _tooltipExpandRight
            ? _tooltipNativeAnchorMin.X
            : _tooltipNativeAnchorMax.X - tooltipWidth;
        var desiredY = _tooltipNativeAnchorMin.Y;

        var maxX = MathF.Max(nativePadding.X, nativeDisplaySize.X - tooltipWidth - nativePadding.X);
        var maxY = MathF.Max(nativePadding.Y, nativeDisplaySize.Y - tooltipHeight - nativePadding.Y);
        desiredX = Math.Clamp(desiredX, nativePadding.X, maxX);
        desiredY = Math.Clamp(desiredY, nativePadding.Y, maxY);

        targetX = (short)Math.Clamp((int)MathF.Round(desiredX), short.MinValue, short.MaxValue);
        targetY = (short)Math.Clamp((int)MathF.Round(desiredY), short.MinValue, short.MaxValue);
        return true;
    }

    private void MaybeLogItemDetailRepositionFailure(string message)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastItemDetailRepositionFailureLog).TotalSeconds < 5)
        {
            return;
        }

        _lastItemDetailRepositionFailureLog = now;
        GatherBuddy.Log.Debug($"[NativeItemTooltipBridge] {message}");
    }

    private static bool TryCheckNativeTooltipReadiness(out string reason)
    {
        if (!Dalamud.ClientState.IsLoggedIn)
        {
            reason = "client is not logged in";
            return false;
        }

        if (Dalamud.Objects.LocalPlayer == null)
        {
            reason = "local player is unavailable";
            return false;
        }

        if (Functions.BetweenAreas())
        {
            reason = "player is transitioning between areas";
            return false;
        }

        if (!GenericHelpers.IsScreenReady())
        {
            reason = "screen is not ready";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool TryPrepareAnchor(out ushort parentAddonId, out AtkResNode* anchorNode, out nint anchorHostAddonAddress)
    {
        parentAddonId = 0;
        anchorNode = null;
        anchorHostAddonAddress = 0;
        if (!TryCheckNativeTooltipReadiness(out _))
        {
            return false;
        }

        if (!TryGetHostAddon(out var hostAddon))
        {
            MaybeLogHostFailure();
            return false;
        }

        if (hostAddon->RootNode == null)
        {
            MaybeLogHostFailure("Host addon root node is unavailable; skipping native item tooltip.");
            return false;
        }

        parentAddonId = hostAddon->Id;
        anchorNode = hostAddon->RootNode;
        anchorHostAddonAddress = (nint)hostAddon;
        return parentAddonId != 0;
    }

    private static bool TryGetHostAddon(out AtkUnitBase* hostAddon)
        => GenericHelpers.TryGetAddonByName(HostAddonName, out hostAddon) && IsUsableHost(hostAddon);

    private static bool IsUsableHost(AtkUnitBase* hostAddon)
        => hostAddon != null
         && hostAddon->Id != 0
         && hostAddon->RootNode != null;

    private void UpdateAnchorBounds(Vector2 rectMin, Vector2 rectMax)
    {
        var nativeMin = ToNativeUi(rectMin);
        var nativeMax = ToNativeUi(rectMax);
        var size = nativeMax - nativeMin;
        if (size.X < 1f)
        {
            size.X = 1f;
        }

        if (size.Y < 1f)
        {
            size.Y = 1f;
        }

        _tooltipNativeAnchorMin = nativeMin;
        _tooltipNativeAnchorMax = nativeMin + size;
    }

    private void HideTooltip()
    {
        if (_tooltipVisible)
        {
            var stage = AtkStage.Instance();
            if (stage != null)
            {
                try
                {
                    stage->TooltipManager.HideTooltip(_tooltipParentAddonId);
                }
                catch (Exception ex)
                {
                    GatherBuddy.Log.Warning($"[NativeItemTooltipBridge] Failed to hide tooltip: {ex.Message}");
                }
            }
        }

        ClearTooltipState();
    }

    private void ClearTooltipState()
    {
        _tooltipVisible = false;
        _tooltipParentAddonId = 0;
        _tooltipItemId = 0;
        _anchorNode = null;
        _anchorHostAddonAddress = 0;
        _tooltipAnchorMin = new Vector2(float.NaN);
        _tooltipAnchorMax = new Vector2(float.NaN);
        _tooltipNativeAnchorMin = new Vector2(float.NaN);
        _tooltipNativeAnchorMax = new Vector2(float.NaN);
        _tooltipExpandRight = false;
    }

    private void MaybeLogHostFailure(string? message = null)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastHostFailureLog).TotalSeconds < 5)
        {
            return;
        }

        _lastHostFailureLog = now;
        message ??= $"Host addon {HostAddonName} is unavailable; skipping native item tooltip.";
        GatherBuddy.Log.Debug($"[NativeItemTooltipBridge] {message}");
    }

    private bool IsCurrentHostAnchorValid()
    {
        if (_anchorNode == null || _anchorHostAddonAddress == 0 || _tooltipParentAddonId == 0)
        {
            return false;
        }

        if (!TryGetHostAddon(out var hostAddon))
        {
            return false;
        }

        return (nint)hostAddon == _anchorHostAddonAddress
            && hostAddon->Id == _tooltipParentAddonId
            && hostAddon->RootNode == _anchorNode;
    }

    private static Vector2 ToNativeUi(Vector2 position)
    {
        var scale = AtkUnitBase.GetGlobalUIScale();
        if (scale <= 0f)
        {
            scale = 1f;
        }

        return position / scale;
    }

    private static bool RectsEqual(Vector2 left, Vector2 right)
        => MathF.Abs(left.X - right.X) <= AnchorEpsilon
        && MathF.Abs(left.Y - right.Y) <= AnchorEpsilon;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void TooltipPositionCallback(float* screenX, float* screenY, AtkResNode* node)
    {
        if (screenX == null || screenY == null || node == null)
        {
            return;
        }

        var bridge = GatherBuddy.NativeItemTooltipBridge;
        if (bridge == null || bridge._anchorNode == null || node != bridge._anchorNode)
        {
            *screenX = node->ScreenX;
            *screenY = node->ScreenY;
            return;
        }

        if (float.IsNaN(bridge._tooltipNativeAnchorMin.X) || float.IsNaN(bridge._tooltipNativeAnchorMin.Y) || float.IsNaN(bridge._tooltipNativeAnchorMax.X) || float.IsNaN(bridge._tooltipNativeAnchorMax.Y))
        {
            *screenX = node->ScreenX;
            *screenY = node->ScreenY;
            return;
        }

        *screenX = bridge._tooltipExpandRight ? bridge._tooltipNativeAnchorMin.X : bridge._tooltipNativeAnchorMax.X;
        *screenY = bridge._tooltipNativeAnchorMin.Y;
    }
}
