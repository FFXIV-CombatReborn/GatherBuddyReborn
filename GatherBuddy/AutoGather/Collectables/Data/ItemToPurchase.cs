namespace GatherBuddy.AutoGather.Collectables.Data;

public class ItemToPurchase
{
    public ScripShopItem Item { get; set; }
    public string Name => Item.Name;
    public int Quantity { get; set; }
}
