using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using FFXIVClientStructs.STD;

namespace GatherBuddy.AutoGather.Collectables;

public unsafe abstract class TreeListWindowBase
{
    protected readonly StdVector<Pointer<AtkComponentTreeListItem>> Items;
    protected readonly string[] Labels;

    protected TreeListWindowBase(AtkUnitBase* addon)
    {
        var treeList = FindTreeList(addon);
        Items = treeList->Items;
        Labels = new string[(int)treeList->Items.Count];
        PopulateLabels();
    }

    protected abstract bool IsTargetNode(AtkResNode* node);

    protected abstract string ExtractLabel(AtkComponentTreeListItem* item);

    private AtkComponentTreeList* FindTreeList(AtkUnitBase* addon)
    {
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (!IsTargetNode(node)) continue;

            var compNode = node->GetAsAtkComponentNode();
            if (compNode == null || compNode->Component == null) continue;

            return (AtkComponentTreeList*)compNode->Component;
        }

        return null;
    }

    private void PopulateLabels()
    {
        for (var i = 0; i < Labels.Length; i++)
        {
            var item = Items[i].Value;
            Labels[i] = item != null ? ExtractLabel(item) : string.Empty;
        }
    }
}
