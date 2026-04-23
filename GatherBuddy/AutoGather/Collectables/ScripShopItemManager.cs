using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using GatherBuddy.AutoGather.Collectables.Data;

namespace GatherBuddy.AutoGather.Collectables;

public class ScripShopItemManager
{
    public static List<ScripShopItem> ShopItems = new();
    public static bool IsLoading { get; private set; }

    public ScripShopItemManager()
    {
        _ = LoadScripItemsAsync();
    }

    public async Task LoadScripItemsAsync()
    {
        IsLoading = true;
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "GatherBuddy.CustomInfo.ScripShopItems.json";
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                var filePath = Path.Combine(Dalamud.PluginInterface.ConfigDirectory.FullName, "ScripShopItems.json");
                if (File.Exists(filePath))
                {
                    var text = await File.ReadAllTextAsync(filePath);
                    ShopItems = JsonSerializer.Deserialize<List<ScripShopItem>>(text) ?? new();
                }
                else
                {
                    GatherBuddy.Log.Error($"[ScripShopItemManager] Could not find embedded resource or file: {resourceName}");
                    ShopItems = new();
                }
            }
            else
            {
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync();
                ShopItems = JsonSerializer.Deserialize<List<ScripShopItem>>(text) ?? new();
            }
        }
        catch (Exception ex)
        {
            ShopItems = new();
            GatherBuddy.Log.Error($"[ScripShopItemManager] Failed to load scrip shop items: {ex}");
        }
        finally
        {
            IsLoading = false;
        }
    }
}
