using System;
using System.Collections.Generic;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using GatherBuddy.AutoGather.Collectables;

namespace GatherBuddy.Automation;

public unsafe abstract class AddonMasterBase<T> where T : unmanaged
{
    protected AddonMasterBase(nint addon)
    {
        Addon = (T*)addon;
    }

    protected AddonMasterBase(void* addon)
    {
        Addon = (T*)addon;
    }

    public T* Addon { get; }
    public AtkUnitBase* Base => (AtkUnitBase*)Addon;

    protected bool ClickButtonIfEnabled(AtkComponentButton* button)
    {
        if (button == null)
            return false;

        if (button->IsEnabled && button->AtkComponentBase.OwnerNode->AtkResNode.IsVisible())
        {
            var btnRes = button->AtkComponentBase.OwnerNode->AtkResNode;
            var evt = (AtkEvent*)btnRes.AtkEventManager.Event;
            Base->ReceiveEvent(AtkEventType.MouseClick, (int)evt->Param, evt);
            return true;
        }
        return false;
    }

    protected AtkEvent CreateAtkEvent(byte flags = 0)
    {
        var evt = new AtkEvent();
        evt.Listener = (AtkEventListener*)Base;
        evt.Target = &AtkStage.Instance()->AtkEventTarget;
        evt.Param = 0;
        evt.NextEvent = null;
        return evt;
    }
}

public static unsafe class AddonMaster
{
    public class SelectYesno : AddonMasterBase<AddonSelectYesno>
    {
        public SelectYesno(nint addon) : base(addon) { }
        public SelectYesno(void* addon) : base(addon) { }

        public string TextLegacy => Addon->PromptText != null ? System.Text.RegularExpressions.Regex.Replace(MemoryHelper.ReadSeString(&Addon->PromptText->NodeText).TextValue, @"\s+", " ").Trim() : string.Empty;

        public void Yes()
        {
            if (Addon->YesButton != null)
            {
                if (!Addon->YesButton->IsEnabled)
                {
                    GatherBuddy.Log.Debug($"[AddonMaster.SelectYesno] Force enabling Yes button");
                    var flagsPtr = (ushort*)&Addon->YesButton->AtkComponentBase.OwnerNode->AtkResNode.NodeFlags;
                    *flagsPtr ^= 1 << 5;
                }
                Callback.Fire(Base, true, 0);
            }
        }

        public void No() => ClickButtonIfEnabled(Addon->NoButton);
    }

    public class SelectString : AddonMasterBase<AddonSelectString>
    {
        public SelectString(nint addon) : base(addon) { }
        public SelectString(void* addon) : base(addon) { }

        public int EntryCount => Addon->PopupMenu.PopupMenu.EntryCount;

        public Entry[] Entries
        {
            get
            {
                var ret = new Entry[EntryCount];
                for (var i = 0; i < ret.Length; i++)
                {
                    ret[i] = new Entry(this, i);
                }
                return ret;
            }
        }

        public struct Entry
        {
            private readonly SelectString _parent;
            public int Index { get; init; }

            public Entry(SelectString parent, int index)
            {
                _parent = parent;
                Index = index;
            }

            public void Select()
            {
                Callback.Fire(_parent.Base, true, Index);
            }
        }
    }

    public class Talk : AddonMasterBase<AddonTalk>
    {
        public Talk(nint addon) : base(addon) { }
        public Talk(void* addon) : base(addon) { }

        public void Click()
        {
            var evt = stackalloc AtkEvent[1]
            {
                CreateAtkEvent(132),
            };
            var data = stackalloc AtkEventData[1];
            for (var i = 0; i < sizeof(AtkEventData); i++)
            {
                ((byte*)data)[i] = 0;
            }
            Base->ReceiveEvent(AtkEventType.MouseDown, 0, evt, data);
            Base->ReceiveEvent(AtkEventType.MouseClick, 0, evt, data);
            Base->ReceiveEvent(AtkEventType.MouseUp, 0, evt, data);
        }
    }

    public class Repair : AddonMasterBase<AddonRepair>
    {
        public Repair(nint addon) : base(addon) { }
        public Repair(void* addon) : base(addon) { }

        public void RepairAll()
        {
            var btn = Addon->RepairAllButton;
            GatherBuddy.Log.Debug($"[Repair] RepairAllButton: null={btn == null}, enabled={btn != null && btn->IsEnabled}, visible={btn != null && btn->AtkComponentBase.OwnerNode->AtkResNode.IsVisible()}");
            if (btn != null && Base != null && Base->IsReady)
            {
                Callback.Fire(Base, true, 1);
                GatherBuddy.Log.Debug("[Repair] Fired callback(1) on Repair addon");
            }
        }
    }

    public class ContentsFinderConfirm : AddonMasterBase<AddonContentsFinderConfirm>
    {
        public ContentsFinderConfirm(nint addon) : base(addon) { }
        public ContentsFinderConfirm(void* addon) : base(addon) { }

        public void Commence()
        {
            if (Addon->CommenceButton != null && Addon->CommenceButton->IsEnabled && Addon->CommenceButton->AtkComponentBase.OwnerNode->AtkResNode.IsVisible())
                Callback.Fire(Base, true, 8);
        }
        
        public void Withdraw() => ClickButtonIfEnabled(Addon->WithdrawButton);
        public void Wait() => ClickButtonIfEnabled(Addon->WaitButton);
    }

    public class PurifyResult : AddonMasterBase<AtkUnitBase>
    {
        public PurifyResult(nint addon) : base(addon) { }
        public PurifyResult(void* addon) : base(addon) { }

        public SeString BannerSeString => MemoryHelper.ReadSeString(&Base->GetTextNodeById(2)->NodeText);
        public string BannerText => BannerSeString.ToString();
        public AtkComponentButton* AutomaticButton => Addon->GetComponentButtonById(19);
        public AtkComponentButton* CloseButton => Addon->GetComponentButtonById(20);

        public void Automatic() => ClickButtonIfEnabled(AutomaticButton);
        public void Close() => ClickButtonIfEnabled(CloseButton);
    }

    public class MaterializeDialog : AddonMasterBase<AtkUnitBase>
    {
        public MaterializeDialog(nint addon) : base(addon) { }
        public MaterializeDialog(void* addon) : base(addon) { }
        public MaterializeDialog(AtkUnitBase* addon) : base((nint)addon) { }

        public void Materialize()
        {
            if (Base != null && Base->IsReady)
            {
                Callback.Fire(Base, true, 0);
            }
        }
    }

    public class InclusionShop : AddonMasterBase<AtkUnitBase>
    {
        public InclusionShop(nint addon) : base(addon) { }
        public InclusionShop(void* addon) : base(addon) { }
        public InclusionShop(AtkUnitBase* addon) : base((nint)addon) { }

        public ShopItem[] ShopItems
        {
            get
            {
                if (Base == null || !Base->IsReady)
                    return Array.Empty<ShopItem>();

                var result = new List<ShopItem>();
                foreach (var item in ScripShopItemManager.ShopItems)
                {
                    result.Add(new ShopItem { ItemId = item.ItemId, Index = item.Index });
                }
                
                return result.ToArray();
            }
        }

        public struct ShopItem
        {
            public uint ItemId;
            public int Index;
        }
    }
}
