using Newtonsoft.Json;

namespace GatherBuddy.AutoHookIntegration.Models;

public class AHLuresConfig
{
    [JsonProperty("Enabled")]
    public bool Enabled { get; set; }

    [JsonProperty("AmbitiousLureEnabled")]
    public bool AmbitiousLureEnabled { get; set; }
    
    [JsonProperty("AmbitiousLureGpThreshold")]
    public int AmbitiousLureGpThreshold { get; set; }
    
    [JsonProperty("AmbitiousLureGpThresholdAbove")]
    public bool AmbitiousLureGpThresholdAbove { get; set; }

    [JsonProperty("ModestLureEnabled")]
    public bool ModestLureEnabled { get; set; }
    
    [JsonProperty("ModestLureGpThreshold")]
    public int ModestLureGpThreshold { get; set; }
    
    [JsonProperty("ModestLureGpThresholdAbove")]
    public bool ModestLureGpThresholdAbove { get; set; }
}
