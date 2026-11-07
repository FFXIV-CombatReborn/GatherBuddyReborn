using Newtonsoft.Json;
using System.Collections.Generic;

namespace GatherBuddy.AutoHookIntegration.Models;

public class AHConditionSet
{
    [JsonProperty("m")]
    public int CombineMode { get; set; }

    [JsonProperty("g")]
    public List<AHConditionGroup> Groups { get; set; } = [];

    [JsonProperty("e")]
    public string? Expression { get; set; }
}

public class AHConditionGroup
{
    [JsonProperty("m")]
    public int CombineMode { get; set; }

    [JsonProperty("c")]
    public List<AHCondition> Conditions { get; set; } = [];

    [JsonProperty("a")]
    public bool Enabled { get; set; } = true;
}

public class AHCondition
{
    [JsonProperty("t")]
    public string Type { get; set; }

    [JsonProperty("p")]
    public Dictionary<string, object> Parameters { get; set; }

    [JsonProperty("e")]
    public bool Enabled { get; set; } = true;

    public AHCondition(string type, Dictionary<string, object> parameters)
    {
        Type = type;
        Parameters = parameters;
    }
}
