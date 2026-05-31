using System;
using System.Collections.Generic;

namespace GatherBuddy.Deimos;

/// named time-throttle (EzThrottler replacement)
internal static class DeimosThrottle
{
    private static readonly Dictionary<string, long> Next = new();

    public static bool Throttle(string name, int ms = 500)
    {
        var now = Environment.TickCount64;
        if (Next.TryGetValue(name, out var allowedAt) && now < allowedAt)
            return false;

        Next[name] = now + ms;
        return true;
    }

    public static void Reset(string name) => Next.Remove(name);

    public static void Clear() => Next.Clear();
}
