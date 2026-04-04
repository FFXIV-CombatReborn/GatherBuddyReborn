using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using GatherBuddy.Vulcan;

namespace GatherBuddy.Crafting;

public class RaphaelSolveCoordinator
{
    private readonly RaphaelSolveCoordinatorConfig _config;
    private readonly ConcurrentDictionary<string, CachedRaphaelSolution> _cachedSolutions = new();
    private readonly ConcurrentDictionary<string, SolveTask> _inProgressTasks = new();
    private readonly Queue<RaphaelSolveRequest> _pendingQueue = new();
    private int _activeSolveCount = 0;

    private record SolveTask(CancellationTokenSource CTS, Task Task);

    private const string CacheFileName = "raphael_solution_cache.json";

    public RaphaelSolveCoordinator(RaphaelSolveCoordinatorConfig? config = null)
    {
        _config = config ?? new RaphaelSolveCoordinatorConfig();
        Load();
    }

    public void Save()
    {
        try
        {
            var file = Functions.ObtainSaveFile(CacheFileName);
            if (file == null)
                return;

            var toSave = _cachedSolutions.Values
                .Where(s => !s.IsFailed)
                .ToList();

            File.WriteAllText(file.FullName, JsonConvert.SerializeObject(toSave, Formatting.Indented));
            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Saved {toSave.Count} solutions to cache");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[RaphaelSolveCoordinator] Failed to save solution cache: {ex.Message}");
        }
    }

    private void Load()
    {
        try
        {
            var file = Functions.ObtainSaveFile(CacheFileName);
            if (file == null || !file.Exists)
                return;

            var solutions = JsonConvert.DeserializeObject<List<CachedRaphaelSolution>>(File.ReadAllText(file.FullName));
            if (solutions == null)
                return;

            var cutoff = DateTime.UtcNow.AddDays(-_config.SolutionCacheMaxAgeDays);
            var loaded = 0;
            foreach (var solution in solutions)
            {
                if (solution.IsFailed || solution.GeneratedAt < cutoff)
                    continue;
                _cachedSolutions.TryAdd(solution.Key, solution);
                loaded++;
            }

            GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Loaded {loaded}/{solutions.Count} solutions from cache ({solutions.Count - loaded} expired/skipped)");
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[RaphaelSolveCoordinator] Failed to load solution cache: {ex.Message}");
        }
    }

    public int PendingSolves => _inProgressTasks.Count + _pendingQueue.Count;
    public int ActiveSolves => _activeSolveCount;
    public int CachedSolutionCount => _cachedSolutions.Count;

    public void EnqueueSolvesFromRequests(IEnumerable<RaphaelSolveRequest> requests)
    {
        if (!_config.RaphaelEnabled)
        {
            GatherBuddy.Log.Debug("[RaphaelSolveCoordinator] Raphael solver disabled, skipping enqueue");
            return;
        }

        var requestList = requests.ToList();
        if (requestList.Count == 0)
        {
            GatherBuddy.Log.Debug("[RaphaelSolveCoordinator] No requests provided for Raphael enqueue");
            return;
        }

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Starting Raphael enqueue with {requestList.Count} requests");

        var uniqueCrafts = new Dictionary<string, RaphaelSolveRequest>();
        foreach (var request in requestList)
        {
            var key = request.GetKey();
            uniqueCrafts.TryAdd(key, request);
        }

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Extracted {uniqueCrafts.Count} unique crafts from {requestList.Count} requests");
        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Enqueuing {uniqueCrafts.Count} unique crafts for Raphael solving (max concurrent: {_config.MaxConcurrentRaphaelProcesses})");

        foreach (var craft in uniqueCrafts.Values)
        {
            var key = craft.GetKey();
            if (_cachedSolutions.ContainsKey(key))
            {
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Recipe {craft.RecipeId} already cached");
            }
            else if (_inProgressTasks.ContainsKey(key))
            {
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Recipe {craft.RecipeId} already in progress");
            }
            else
            {
                _pendingQueue.Enqueue(craft);
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Queued recipe {craft.RecipeId} for solving (key: {key})");
            }
        }

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Queue prepared: {_pendingQueue.Count} pending, {_inProgressTasks.Count} in progress, {_cachedSolutions.Count} cached");
        ProcessPendingQueue();
    }

    public void EnqueueSolvesForJobs(IEnumerable<CraftingListItem> queue, Dictionary<uint, GameStateBuilder.PlayerStats> jobStatsMap)
    {
        if (!_config.RaphaelEnabled)
        {
            GatherBuddy.Log.Debug("[RaphaelSolveCoordinator] Raphael solver disabled, skipping enqueue");
            return;
        }

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Starting Raphael enqueue for {jobStatsMap.Count} unique jobs");

        var uniqueCrafts = new Dictionary<string, RaphaelSolveRequest>();
        var queueList = queue.ToList();
        var recipeSheet = Dalamud.GameData.GetExcelSheet<Recipe>();
        if (recipeSheet == null)
        {
            GatherBuddy.Log.Error("[RaphaelSolveCoordinator] Failed to get recipe sheet");
            return;
        }

        foreach (var item in queueList)
        {
            if (!recipeSheet.TryGetRow(item.RecipeId, out var recipe))
            {
                GatherBuddy.Log.Warning($"[RaphaelSolveCoordinator] Recipe {item.RecipeId} not found");
                continue;
            }

            var jobId = (uint)(recipe.CraftType.RowId + 8);
            if (!jobStatsMap.TryGetValue(jobId, out var stats))
            {
                GatherBuddy.Log.Warning($"[RaphaelSolveCoordinator] No stats found for job {jobId}, skipping recipe {item.RecipeId}");
                continue;
            }

            var request = new RaphaelSolveRequest(
                RecipeId: item.RecipeId,
                Level: stats.Level,
                Craftsmanship: stats.Craftsmanship,
                Control: stats.Control,
                CP: stats.CP,
                Manipulation: stats.Manipulation,
                Specialist: stats.Specialist,
                InitialQuality: CalculateInitialQuality(item, recipe)
            );

            var key = request.GetKey();
            uniqueCrafts.TryAdd(key, request);
        }

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Extracted {uniqueCrafts.Count} unique crafts from queue of {queueList.Count} items");
        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Enqueuing {uniqueCrafts.Count} unique crafts for Raphael solving (max concurrent: {_config.MaxConcurrentRaphaelProcesses})");

        foreach (var craft in uniqueCrafts.Values)
        {
            var key = craft.GetKey();
            if (_cachedSolutions.ContainsKey(key))
            {
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Recipe {craft.RecipeId} already cached");
            }
            else if (_inProgressTasks.ContainsKey(key))
            {
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Recipe {craft.RecipeId} already in progress");
            }
            else
            {
                _pendingQueue.Enqueue(craft);
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Queued recipe {craft.RecipeId} for solving (key: {key})");
            }
        }

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Queue prepared: {_pendingQueue.Count} pending, {_inProgressTasks.Count} in progress, {_cachedSolutions.Count} cached");
        ProcessPendingQueue();
    }

    public void EnqueueSolvesFromCraftStates(IEnumerable<CraftingListItem> queue, List<(uint RecipeId, int Craftsmanship, int Control, int CP, int Level, bool Manipulation, bool Specialist)> recipeStats)
    {
        if (!_config.RaphaelEnabled)
        {
            GatherBuddy.Log.Debug("[RaphaelSolveCoordinator] Raphael solver disabled, skipping enqueue");
            return;
        }

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Starting Raphael enqueue with CraftState-derived stats for {recipeStats.Count} recipes");

        var uniqueCrafts = new Dictionary<string, RaphaelSolveRequest>();
        foreach (var item in queue)
        {
            var stats = recipeStats.FirstOrDefault(s => s.RecipeId == item.RecipeId);
            if (stats.RecipeId == 0)
                continue;
            
            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe == null)
                continue;
            
            var initialQuality = CalculateInitialQuality(item, recipe.Value);

            var request = new RaphaelSolveRequest(
                RecipeId: stats.RecipeId,
                Level: stats.Level,
                Craftsmanship: stats.Craftsmanship,
                Control: stats.Control,
                CP: stats.CP,
                Manipulation: stats.Manipulation,
                Specialist: stats.Specialist,
                InitialQuality: initialQuality
            );

            var key = request.GetKey();
            uniqueCrafts.TryAdd(key, request);
        }

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Extracted {uniqueCrafts.Count} unique crafts from queue of {queue.Count()} items");
        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Enqueuing {uniqueCrafts.Count} unique crafts for Raphael solving (max concurrent: {_config.MaxConcurrentRaphaelProcesses})");

        foreach (var craft in uniqueCrafts.Values)
        {
            var key = craft.GetKey();
            if (_cachedSolutions.ContainsKey(key))
            {
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Recipe {craft.RecipeId} already cached");
            }
            else if (_inProgressTasks.ContainsKey(key))
            {
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Recipe {craft.RecipeId} already in progress");
            }
            else
            {
                _pendingQueue.Enqueue(craft);
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Queued recipe {craft.RecipeId} for solving (key: {key})");
            }
        }

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Queue prepared: {_pendingQueue.Count} pending, {_inProgressTasks.Count} in progress, {_cachedSolutions.Count} cached");
        ProcessPendingQueue();
    }

    public void EnqueueSolves(IEnumerable<CraftingListItem> queue, int playerCraftsmanship, int playerControl, int playerCP, int playerLevel, bool manipulationUnlocked, bool isSpecialist)
    {
        if (!_config.RaphaelEnabled)
        {
            GatherBuddy.Log.Debug("[RaphaelSolveCoordinator] Raphael solver disabled, skipping enqueue");
            return;
        }

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Starting Raphael enqueue: Craftsmanship={playerCraftsmanship}, Control={playerControl}, CP={playerCP}, Level={playerLevel}, Manipulation={manipulationUnlocked}, Specialist={isSpecialist}");

        var uniqueCrafts = ExtractUniqueCrafts(queue, playerCraftsmanship, playerControl, playerCP, playerLevel, manipulationUnlocked, isSpecialist);
        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Extracted {uniqueCrafts.Count} unique crafts from queue of {queue.Count()} items");

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Enqueuing {uniqueCrafts.Count} unique crafts for Raphael solving (max concurrent: {_config.MaxConcurrentRaphaelProcesses})");

        foreach (var craft in uniqueCrafts)
        {
            var key = craft.GetKey();
            if (_cachedSolutions.ContainsKey(key))
            {
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Recipe {craft.RecipeId} already cached");
            }
            else if (_inProgressTasks.ContainsKey(key))
            {
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Recipe {craft.RecipeId} already in progress");
            }
            else
            {
                _pendingQueue.Enqueue(craft);
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Queued recipe {craft.RecipeId} for solving (key: {key})");
            }
        }

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Queue prepared: {_pendingQueue.Count} pending, {_inProgressTasks.Count} in progress, {_cachedSolutions.Count} cached");
        ProcessPendingQueue();
    }

    public bool TryGetSolution(RaphaelSolveRequest request, out CachedRaphaelSolution? solution)
    {
        var key = request.GetKey();
        solution = null;

        if (_cachedSolutions.TryGetValue(key, out var cached))
        {
            if (cached.IsFailed)
            {
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Solution for {key} previously failed: {cached.FailureReason}");
                return false;
            }

            solution = cached;
            return true;
        }

        return false;
    }

    public IEnumerable<CachedRaphaelSolution> GetAllCachedSolutions()
    {
        return _cachedSolutions.Values.Where(s => !s.IsFailed).ToList();
    }

    public bool HasFailedSolution(RaphaelSolveRequest request, out string? failureReason)
    {
        var key = request.GetKey();
        failureReason = null;
        if (_cachedSolutions.TryGetValue(key, out var cached) && cached.IsFailed)
        {
            failureReason = cached.FailureReason;
            return true;
        }
        return false;
    }

    public bool IsSolveInProgress(RaphaelSolveRequest request)
    {
        return _inProgressTasks.ContainsKey(request.GetKey());
    }

    public bool IsKnown(RaphaelSolveRequest request)
    {
        var key = request.GetKey();
        return _cachedSolutions.ContainsKey(key)
            || _inProgressTasks.ContainsKey(key)
            || _pendingQueue.Any(r => r.GetKey() == key);
    }

    public void ClearIfAutoEnabled()
    {
        if (!_config.AutoClearSolutionCache)
            return;
        GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Auto-clearing solution cache on queue start ({_cachedSolutions.Count} solutions)");
        _cachedSolutions.Clear();
    }

    public void ReenqueueIfMissing(RaphaelSolveRequest request)
    {
        if (!_config.RaphaelEnabled || IsKnown(request))
            return;
        GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Re-enqueueing missing solution for recipe {request.RecipeId}");
        _pendingQueue.Enqueue(request);
        ProcessPendingQueue();
    }

    public void Clear()
    {
        _cachedSolutions.Clear();
        _pendingQueue.Clear();
        foreach (var task in _inProgressTasks.Values)
        {
            task.CTS.Cancel();
        }
        _inProgressTasks.Clear();
        _activeSolveCount = 0;
        Save();
    }

    private List<RaphaelSolveRequest> ExtractUniqueCrafts(
        IEnumerable<CraftingListItem> queue,
        int playerCraftsmanship,
        int playerControl,
        int playerCP,
        int playerLevel,
        bool manipulationUnlocked,
        bool isSpecialist)
    {
        var uniqueCrafts = new Dictionary<string, RaphaelSolveRequest>();

        foreach (var item in queue)
        {
            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe == null)
            {
                GatherBuddy.Log.Warning($"[RaphaelSolveCoordinator] Recipe {item.RecipeId} not found");
                continue;
            }

            var request = new RaphaelSolveRequest(
                RecipeId: item.RecipeId,
                Level: playerLevel,
                Craftsmanship: playerCraftsmanship,
                Control: playerControl,
                CP: playerCP,
                Manipulation: manipulationUnlocked,
                Specialist: isSpecialist,
                InitialQuality: CalculateInitialQuality(item, recipe.Value)
            );

            var key = request.GetKey();
            uniqueCrafts.TryAdd(key, request);
        }

        return uniqueCrafts.Values.ToList();
    }

    private static int CalculateInitialQuality(CraftingListItem item, Recipe recipe)
    {
        item.QualityPolicy ??= CraftingQualityPolicyResolver.Resolve(recipe, item.CraftSettings);
        if (item.IngredientPreferences.Count == 0)
            item.IngredientPreferences = item.QualityPolicy.BuildGuaranteedHQPreferences();
        return item.QualityPolicy.CalculateGuaranteedInitialQuality(recipe);
    }

    private void ProcessPendingQueue()
    {
        while (_pendingQueue.Count > 0 && _activeSolveCount < _config.MaxConcurrentRaphaelProcesses)
        {
            if (_pendingQueue.TryDequeue(out var request))
            {
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Processing pending queue: {_pendingQueue.Count} remaining, {_activeSolveCount}/{_config.MaxConcurrentRaphaelProcesses} active");
                _ = SpawnRaphaelSolveAsync(request);
            }
        }
        if (_pendingQueue.Count > 0)
        {
            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Queue processing paused: {_pendingQueue.Count} pending, {_activeSolveCount}/{_config.MaxConcurrentRaphaelProcesses} active");
        }
    }

    private async Task SpawnRaphaelSolveAsync(RaphaelSolveRequest request)
    {
        var key = request.GetKey();

        if (_inProgressTasks.ContainsKey(key))
        {
            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Solve for {key} already in progress");
            return;
        }

        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMinutes(_config.RaphaelTimeoutMinutes));

        _activeSolveCount++;
        var task = Task.Run(async () =>
        {
            try
            {
                await ExecuteRaphaelSolve(request, cts.Token);
            }
            finally
            {
                _activeSolveCount--;
                _inProgressTasks.TryRemove(key, out _);
                ProcessPendingQueue();
            }
        }, cts.Token);

        var solveTask = new SolveTask(cts, task);
        if (_inProgressTasks.TryAdd(key, solveTask))
        {
            GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Spawned Raphael solve for recipe {request.RecipeId} (key: {key})");
        }
    }

    private async Task ExecuteRaphaelSolve(RaphaelSolveRequest request, CancellationToken ct)
    {
        var key = request.GetKey();
        var cacheEntry = new CachedRaphaelSolution(key, request);
        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Executing Raphael solve for recipe {request.RecipeId} (key: {key})");

        try
        {
            var raphaelPath = GetRaphaelCliPath();
            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Raphael executable path: {raphaelPath}");
            
            if (string.IsNullOrEmpty(raphaelPath))
            {
                cacheEntry.IsFailed = true;
                cacheEntry.FailureReason = "raphael-cli.exe path is empty";
                _cachedSolutions[key] = cacheEntry;
                GatherBuddy.Log.Error("[RaphaelSolveCoordinator] FAIL: raphael-cli.exe path could not be resolved - plugin directory is unavailable");
                return;
            }
            
            if (!File.Exists(raphaelPath))
            {
                cacheEntry.IsFailed = true;
                cacheEntry.FailureReason = $"raphael-cli.exe not found at {raphaelPath}";
                _cachedSolutions[key] = cacheEntry;
                GatherBuddy.Log.Error($"[RaphaelSolveCoordinator] FAIL: raphael-cli.exe not found at {raphaelPath}. Ensure the plugin was downloaded/updated with the latest version.");
                return;
            }

            var args = BuildRaphaelArguments(request);
            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Raphael arguments: {args}");
            
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = raphaelPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Spawning Raphael process for recipe {request.RecipeId}");

            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Starting Raphael process...");
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Raphael process output received ({output.Length} bytes stdout, {error.Length} bytes stderr)");

            ct.ThrowIfCancellationRequested();

            process.WaitForExit();
            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Raphael process exited with code {process.ExitCode}");

            if (process.ExitCode != 0)
            {
                cacheEntry.IsFailed = true;
                cacheEntry.FailureReason = $"Exit code {process.ExitCode}: {error}";
                _cachedSolutions[key] = cacheEntry;
                GatherBuddy.Log.Error($"[RaphaelSolveCoordinator] FAIL: Raphael exited with code {process.ExitCode} for recipe {request.RecipeId}");
                GatherBuddy.Log.Error($"[RaphaelSolveCoordinator] FAIL: Raphael stderr: {error}");
                return;
            }

            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Parsing Raphael output for recipe {request.RecipeId}...");
            var actionIds = ParseRaphaelOutput(output);
            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Parsed {actionIds.Count} action IDs");
            
            if (actionIds.Count == 0)
            {
                cacheEntry.IsFailed = true;
                cacheEntry.FailureReason = "No actions generated";
                _cachedSolutions[key] = cacheEntry;
                GatherBuddy.Log.Error($"[RaphaelSolveCoordinator] FAIL: Raphael generated empty solution for recipe {request.RecipeId}");
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Raphael stdout was: {output}");
                return;
            }

        cacheEntry.ActionIds = actionIds;
            _cachedSolutions[key] = cacheEntry;
            GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] SUCCESS: Raphael solved recipe {request.RecipeId} with {actionIds.Count} actions");
            Save();
        }
        catch (OperationCanceledException)
        {
            cacheEntry.IsFailed = true;
            cacheEntry.FailureReason = "Solve timeout";
            _cachedSolutions[key] = cacheEntry;
            GatherBuddy.Log.Error($"[RaphaelSolveCoordinator] FAIL: Raphael solve timeout for recipe {request.RecipeId} (timeout: {_config.RaphaelTimeoutMinutes} minutes)");
        }
        catch (Exception ex)
        {
            cacheEntry.IsFailed = true;
            cacheEntry.FailureReason = ex.Message;
            _cachedSolutions[key] = cacheEntry;
            GatherBuddy.Log.Error($"[RaphaelSolveCoordinator] FAIL: Raphael solve exception for recipe {request.RecipeId}: {ex.Message}");
            GatherBuddy.Log.Error($"[RaphaelSolveCoordinator] FAIL: Exception details: {ex}");
        }
    }

    private string BuildRaphaelArguments(RaphaelSolveRequest request)
    {
        var args = new StringBuilder();
        args.Append($"solve --recipe-id {request.RecipeId} ");
        args.Append($"--level {request.Level} ");
        args.Append($"--stats {request.Craftsmanship} {request.Control} {request.CP} ");

        if (request.Manipulation)
            args.Append("--manipulation ");

        if (request.InitialQuality > 0)
            args.Append($"--initial {request.InitialQuality} ");

        if (_config.RaphaelBackloadProgress)
            args.Append("--backload-progress ");

        if (_config.RaphaelAllowSpecialistActions && request.Specialist)
            args.Append("--heart-and-soul --quick-innovation ");

        args.Append("--output-variables action_ids");

        return args.ToString();
    }

    private List<uint> ParseRaphaelOutput(string output)
    {
        var actionIds = new List<uint>();

        if (string.IsNullOrWhiteSpace(output))
            return actionIds;

        try
        {
            var cleaned = output.Replace("[", "").Replace("]", "").Replace("\"", "").Trim();
            var parts = cleaned.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                if (uint.TryParse(part.Trim(), out var actionId))
                    actionIds.Add(actionId);
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[RaphaelSolveCoordinator] Failed to parse Raphael output: {ex.Message}");
        }

        return actionIds;
    }

    private string GetRaphaelCliPath()
    {
        try
        {
            var pluginDir = Path.GetDirectoryName(Dalamud.PluginInterface?.AssemblyLocation?.FullName ?? "");
            if (string.IsNullOrEmpty(pluginDir))
                return string.Empty;

            return Path.Combine(pluginDir, "raphael-cli.exe");
        }
        catch
        {
            return string.Empty;
        }
    }
}
