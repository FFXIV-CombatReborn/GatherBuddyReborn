using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace GatherBuddy.Crafting;

internal enum RetainerItemCommand : long
{
    Retrieve = 0,
    RetrieveQuantity = 3,
}

internal static unsafe class RetainerItemCommandDispatcher
{
    private const int InventoryContextEventOffset = 0x28;

    internal static bool TryRetrieve(InventoryType retainerContainer, uint retainerSlot)
        => Dispatch(retainerContainer, retainerSlot, RetainerItemCommand.Retrieve);

    internal static bool TryRetrieveQuantity(InventoryType retainerContainer, uint retainerSlot)
        => Dispatch(retainerContainer, retainerSlot, RetainerItemCommand.RetrieveQuantity);

    private static bool Dispatch(InventoryType container, uint slot, RetainerItemCommand command)
    {
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Retainer);
        if (agent == null)
        {
            GatherBuddy.Log.Debug($"[RetainerItemCommandDispatcher] AgentRetainer was null while dispatching {command} for {container} slot {slot}");
            return false;
        }

        if (!agent->IsAgentActive())
        {
            GatherBuddy.Log.Debug($"[RetainerItemCommandDispatcher] AgentRetainer was inactive while dispatching {command} for {container} slot {slot}");
            return false;
        }

        void* contextEventPtr = (byte*)agent + InventoryContextEventOffset;
        var vtable = *(void***)contextEventPtr;
        var sendCommand = (delegate* unmanaged<void*, uint, InventoryType, ulong, RetainerItemCommand, void>)vtable[0];

        GatherBuddy.Log.Debug($"[RetainerItemCommandDispatcher] Dispatching {command} for {container} slot {slot}");
        sendCommand(contextEventPtr, slot, container, 0, command);
        return true;
    }
}
