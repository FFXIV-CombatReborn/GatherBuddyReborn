using System.Text.Json.Serialization;

namespace GatherBuddy.AutoHookIntegration.Models;

public class BiteTimerData
{
    [JsonPropertyName("itemId")]
    public uint ItemId { get; set; }

    [JsonPropertyName("min")]
    public double Min { get; set; }

    [JsonPropertyName("median")]
    public double Median { get; set; }

    [JsonPropertyName("mean")]
    public double Mean { get; set; }

    [JsonPropertyName("max")]
    public double Max { get; set; }

    [JsonPropertyName("whiskerMin")]
    public double WhiskerMin { get; set; }

    [JsonPropertyName("whiskerMax")]
    public double WhiskerMax { get; set; }

    [JsonPropertyName("q1")]
    public double Q1 { get; set; }

    [JsonPropertyName("q3")]
    public double Q3 { get; set; }
}
