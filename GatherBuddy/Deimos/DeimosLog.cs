namespace GatherBuddy.Deimos;

/// tagged logging facade over GatherBuddy.Log
internal static class DeimosLog
{
    private const string Tag = "[Deimos]";

    public static void Info(string message)    => GatherBuddy.Log.Information($"{Tag} {message}");
    public static void Debug(string message)   => GatherBuddy.Log.Debug($"{Tag} {message}");
    public static void Verbose(string message) => GatherBuddy.Log.Verbose($"{Tag} {message}");
    public static void Warning(string message) => GatherBuddy.Log.Warning($"{Tag} {message}");
    public static void Error(string message)   => GatherBuddy.Log.Error($"{Tag} {message}");
}
