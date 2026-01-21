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
    
    [JsonProperty("SwapPresetIntuitionGain")]
    public bool SwapPresetIntuitionGain { get; set; }
    
    [JsonProperty("PresetToSwapIntuitionGain")]
    public string PresetToSwapIntuitionGain { get; set; } = "-";
    
    [JsonProperty("SwapPresetIntuitionLost")]
    public bool SwapPresetIntuitionLost { get; set; }
    
    [JsonProperty("PresetToSwapIntuitionLost")]
    public string PresetToSwapIntuitionLost { get; set; } = "-";
}
