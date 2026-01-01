using Newtonsoft.Json;

namespace GatherBuddy.AutoHookIntegration.Models;

public class AHExtraCfg
{
    [JsonProperty("Enabled")]
    public bool Enabled { get; set; }
    
    [JsonProperty("ForceBaitSwap")]
    public bool ForceBaitSwap { get; set; }
    
    [JsonProperty("ForcedBaitId")]
    public uint ForcedBaitId { get; set; }
}
