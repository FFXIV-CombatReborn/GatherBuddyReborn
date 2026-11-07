using System;
using System.Collections.Generic;

namespace GatherBuddy.Utilities;

public class Throttler<T> where T : notnull
{
    private readonly Dictionary<T, long> _throttlers = [];

    public IReadOnlyCollection<T> ThrottleNames => _throttlers.Keys;

    public bool Throttle(T name, TimeSpan ts, bool reThrottle = false)
        => Throttle(name, (int)ts.TotalMilliseconds, reThrottle);

    public bool Throttle(T name, int milliseconds = 500, bool reThrottle = false)
    {
        if (!_throttlers.ContainsKey(name))
        {
            _throttlers[name] = Environment.TickCount64 + milliseconds;
            return true;
        }

        if (Environment.TickCount64 > _throttlers[name])
        {
            _throttlers[name] = Environment.TickCount64 + milliseconds;
            return true;
        }

        if (reThrottle)
        {
            _throttlers[name] = Environment.TickCount64 + milliseconds;
        }

        return false;
    }

    public void Reset(T name)
        => _throttlers.Remove(name);

    public bool Check(T name)
    {
        if (!_throttlers.ContainsKey(name))
        {
            return true;
        }

        return Environment.TickCount64 > _throttlers[name];
    }

    public long GetRemainingTime(T name, bool allowNegative = false)
    {
        if (!_throttlers.ContainsKey(name))
        {
            return allowNegative ? -Environment.TickCount64 : 0;
        }

        var remaining = _throttlers[name] - Environment.TickCount64;

        return allowNegative || remaining > 0 ? remaining : 0;
    }
}

public static class Throttler
{
    private static readonly Throttler<string> _instance = new();

    public static IReadOnlyCollection<string> ThrottleNames => _instance.ThrottleNames;

    public static bool Throttle(string name, TimeSpan ts, bool reThrottle = false)
        => _instance.Throttle(name, ts, reThrottle);

    public static bool Throttle(string name, int milliseconds = 500, bool reThrottle = false)
        => _instance.Throttle(name, milliseconds, reThrottle);

    public static void Reset(string name)
        => _instance.Reset(name);

    public static bool Check(string name)
        => _instance.Check(name);

    public static long GetRemainingTime(string name, bool allowNegative = false)
        => _instance.GetRemainingTime(name, allowNegative);
}
