using System;
using System.Collections.Generic;

namespace GatherBuddy.Deimos.Data;

/// what kind of mission this is + how it scores
[Flags]
public enum MissionAttributes
{
    None = 0,

    Craft = 1,
    Gather = 2,
    Fish = 4,

    Limited = 8,

    Collectables = 16,
    ReducedItems = 32,
    ExpertCraft = 64,

    ScoreTimeRemaining = 128,
    ScoreChains = 256,
    ScoreGatherersBoon = 512,
    ScoreLargestSize = 1024,
    ScoreVariety = 2048,
    ScoreScore = 4096,

    Critical = 8192,
    ProvisionalTimed = 16384,
    ProvisionalWeather = 32768,
    ProvisionalSequential = 65536,

    GreaterReachGather = 131072,
    GreaterReachChain = 262144,
    GreaterReachBoon = 524288,
}

public enum CosmicWeather
{
    None,
    UmbralWind,
    MoonDust,
    Clouds,
    Rain,
    ClearSkies,
    FairSkies,
}

public enum MissionStatus
{
    None,
    Completed,
    Gold,
}

/// cosmic job-id groupings (CRP..CUL / MIN BTN FSH)
internal static class CosmicJobs
{
    public static readonly List<uint> Crafters  = [8, 9, 10, 11, 12, 13, 14, 15];
    public static readonly List<uint> Gatherers = [16, 17, 18];
    public static readonly List<uint> Supported = [8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18];

    public static bool IsCrafter(uint job)  => Crafters.Contains(job);
    public static bool IsGatherer(uint job) => Gatherers.Contains(job);
}
