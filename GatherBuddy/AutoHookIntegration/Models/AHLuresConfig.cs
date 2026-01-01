using Newtonsoft.Json;

namespace GatherBuddy.AutoHookIntegration.Models;

public class AHLuresConfig
{
    [JsonProperty("Enabled")]
    public bool Enabled { get; set; }

    [JsonProperty("Id")]
    public uint Id { get; set; }
    
    [JsonProperty("GpThreshold")]
    public int GpThreshold { get; set; }
    
    [JsonProperty("GpThresholdAbove")]
    public bool GpThresholdAbove { get; set; }

    [JsonProperty("LureStacks")]
    public int LureStacks { get; set; } = 3;
    
    [JsonProperty("CancelAttempt")]
    public bool CancelAttempt { get; set; }
    
    [JsonProperty("LureTarget")]
    public int LureTarget { get; set; }
    
    [JsonProperty("OnlyWhenActiveSlap")]
    public bool OnlyWhenActiveSlap { get; set; }
    
    [JsonProperty("OnlyWhenNotActiveSlap")]
    public bool OnlyWhenNotActiveSlap { get; set; }
    
    [JsonProperty("OnlyWhenActiveIdentical")]
    public bool OnlyWhenActiveIdentical { get; set; }
    
    [JsonProperty("OnlyWhenNotActiveIdentical")]
    public bool OnlyWhenNotActiveIdentical { get; set; }
    
    [JsonProperty("OnlyCastLarge")]
    public bool OnlyCastLarge { get; set; }
}
