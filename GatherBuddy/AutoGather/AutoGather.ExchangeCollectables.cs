using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.CustomInfo;

namespace GatherBuddy.AutoGather
{
    public unsafe partial class AutoGather
    {
        public unsafe CollectablesShop* CollectablesShop
            => GetAddon<CollectablesShop>("CollectablesShop");

        private unsafe bool InteractWithCollectablesShop()
        {
            if (IsPathing || IsPathGenerating) return false;
            var targetSystem = TargetSystem.Instance();
            //工票交易员
            IGameObject? gameObject = Svc.Objects.Where(x => x.Position == new Vector3(-257.2519f, 16.2f, 37.64392f)).FirstOrDefault();
            if (targetSystem == null || gameObject == null || gameObject.Position.DistanceToPlayer() > 4)
                return false;
            
            targetSystem->OpenObjectInteraction((GameObject*)gameObject.Address);
            return true;
        }
        //TODO
        private unsafe bool ExchangeCollectableHelper()
        {
            AtkUnitBase* collectablesShop = (AtkUnitBase*)CollectablesShop;
            if (collectablesShop == null || !collectablesShop->IsVisible) return false;

            AtkComponentList* collectablesTypeList = collectablesShop->GetComponentNodeById(28)->GetAsAtkComponentList();
            if (collectablesTypeList == null) return false;

            for (var i = 0; i < collectablesTypeList->ListLength; i++)
            {
                collectablesTypeList->GetItemRenderer(i);
            }
            //var collectablesShopAddonMaster = new AddonMaster.CollectablesShop(CollectablesShop);
            ////if(!collectablesShopAddonMaster.MinerButton->IsEnabled || !collectablesShopAddonMaster.BotanistButton->IsEnabled)
            ////{
            ////    AutoStatus = "采矿或伐木未开启收藏品";
            ////    Enabled = false;
            ////    return true;
            ////}

                //if (!collectablesShopAddonMaster.MinerButton->IsEnabled)
                //{
                //    //if (!collectablesShopAddonMaster.MinerButton->IsSelected)
                //    //{
                //    //    try
                //    //    {
                //    //        collectablesShopAddonMaster.SelectDiscipleTab(Job.MIN);
                //    //    } catch (ArgumentOutOfRangeException ex) 
                //    //    {
                //    //        Dalamud.Chat.PrintError(ex.Message);
                //    //    }
                //    //    return true;
                //    //}
                //    //var treeListComponentNode = collectablesShopAddonMaster.Base->GetComponentByNodeId(28);
                //    Dalamud.Chat.PrintError("hi: ");
                //    return true;
                //}
                //collectablesShopAddonMaster.SelectDiscipleTab(Job.BTN);


            return true;
        }
    }
}
