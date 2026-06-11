using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GatherBuddy.Deimos.Travel;

/// small shared addon helpers
internal static unsafe class AddonUi
{
    public static bool TryGetVisible(string name, out AtkUnitBase* addon)
    {
        addon = (AtkUnitBase*)Dalamud.GameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsVisible && addon->IsReady;
    }

    // talk boxes advance on a synthetic mouse click
    public static void ClickTalk(AtkUnitBase* talk)
    {
        var evt = stackalloc AtkEvent[1];
        evt[0] = new AtkEvent
        {
            Listener = (AtkEventListener*)talk,
            Target   = &AtkStage.Instance()->AtkEventTarget,
            State    = new AtkEventState { StateFlags = (AtkEventStateFlags)132 },
        };
        var data = stackalloc AtkEventData[1];
        for (var i = 0; i < sizeof(AtkEventData); i++)
            ((byte*)data)[i] = 0;

        talk->ReceiveEvent(AtkEventType.MouseDown, 0, evt, data);
        talk->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
        talk->ReceiveEvent(AtkEventType.MouseUp, 0, evt, data);
    }
}
