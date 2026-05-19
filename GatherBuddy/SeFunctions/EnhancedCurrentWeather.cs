using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.AutoGather.Helpers;
using GatherBuddy.Plugin;
using System;

namespace GatherBuddy.SeFunctions;


public sealed class EnhancedCurrentWeather
{

    public static unsafe byte GetCurrentWeatherId()
    {
        var weatherManager = WeatherManager.Instance();
        if (weatherManager == null)
            return 0;

        var territoryType = Dalamud.ClientState.TerritoryType;
        
        if (territoryType != 0 && weatherManager->HasIndividualWeather((ushort)territoryType))
        {
            var individualWeather = weatherManager->GetIndividualWeather((ushort)territoryType);
            return individualWeather;
        }
        
        var standardWeather = weatherManager->GetCurrentWeather();
        return standardWeather;
    }

    public static unsafe byte GetCurrentWeatherWithDebug()
    {
        var weatherManager = WeatherManager.Instance();
        if (weatherManager == null)
            return 0;

        var territoryType = Dalamud.ClientState.TerritoryType;
        var isInDiadem = Diadem.IsInside;
        
        var currentWeather = weatherManager->GetCurrentWeather();
        
        if (territoryType != 0)
        {
            var hasIndividual = weatherManager->HasIndividualWeather((ushort)territoryType);
            
            if (hasIndividual)
            {
                var individualWeather = weatherManager->GetIndividualWeather((ushort)territoryType);
                
                if (isInDiadem)
                {
                    return individualWeather;
                }
            }
        }
        
        return currentWeather;
    }
    

}