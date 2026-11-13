using Dalamud.Game.ClientState.Keys;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;
using System.Collections.Generic;

namespace GatherBuddy.Config;

public partial class Configuration
{
    public static readonly VirtualKey[] ValidKeys = [VirtualKey.NO_KEY, .. Dalamud.Keys.GetValidVirtualKeys()];

    public const string DefaultIdentifiedGatherableFormat = "Identified {Item} for \"{Input}\".";
    public const string DefaultAlarmFormat = "{Alarm} {Item} {DelayString} at {Location}.";

    public static readonly Dictionary<int, uint> ForegroundColors = CreateForegroundColors();

    private static Dictionary<int, uint> CreateForegroundColors()
    {
        var colors = new Dictionary<int, uint>();
        foreach (var color in Dalamud.GameData.GetExcelSheet<UIColor>()!)
        {
            if ((color.Dark & 0xFF) > 0)
            {
                colors.Add((int)color.RowId, Functions.ReorderColor(color.Dark));
            }
        }

        return colors;
    }

    public const int DefaultSeColorNames = 504;
    public const int DefaultSeColorCommands = 31;
    public const int DefaultSeColorArguments = 546;
    public const int DefaultSeColorAlarm = 518;
}
