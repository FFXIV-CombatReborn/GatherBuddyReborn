using GatherBuddy.Plugin;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GatherBuddy.Crafting;

public class RaphaelSolveCoordinator
{
    private readonly RaphaelSolveCoordinatorConfig _config;
    private readonly ConcurrentDictionary<string, CachedRaphaelSolution> _cachedSolutions = new();
    private readonly ConcurrentDictionary<string, SolveTask> _inProgressTasks = new();
    private readonly Lock _queueLock = new();
    private Queue<RaphaelSolveRequest> _urgentQueue = new();
    private Queue<RaphaelSolveRequest> _backgroundQueue = new();
    private int _activeSolveCount = 0;

    private sealed class SolveTask
    {
        public CancellationTokenSource CTS { get; }
        public Task Task { get; }
        public bool UserCancelled { get; set; }

        public SolveTask(CancellationTokenSource cts, Task task)
        {
            CTS = cts;
            Task = task;
        }
    }

    private const string CacheFileName = "raphael_solution_cache.json";

    public RaphaelSolveCoordinator(RaphaelSolveCoordinatorConfig? config = null)
    {
        _config = config ?? new RaphaelSolveCoordinatorConfig();
        Load();
    }

    private static async Task DrainProcessOutputAsync(Task<string>? outputTask, Task<string>? errorTask)
    {
        if (outputTask != null)
        {
            try
            {
                await outputTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (errorTask != null)
        {
            try
            {
                await errorTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static void TryKillRaphaelProcess(Process? process, uint recipeId, string reason)
    {
        if (process == null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Killing Raphael process for recipe {recipeId} due to {reason}");
            process.Kill();
            process.WaitForExit(2000);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Failed to kill Raphael process for recipe {recipeId}: {ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            var file = Functions.ObtainSaveFile(CacheFileName);
            if (file == null)
            {
                return;
            }

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
            {
                return;
            }

            var solutions = JsonConvert.DeserializeObject<List<CachedRaphaelSolution>>(File.ReadAllText(file.FullName));
            if (solutions == null)
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddDays(-_config.SolutionCacheMaxAgeDays);
            var loaded = 0;
            foreach (var solution in solutions)
            {
                if (solution.IsFailed || solution.GeneratedAt < cutoff)
                {
                    continue;
                }

                _cachedSolutions.TryAdd(solution.Key, solution);
                loaded++;
            }

        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"[RaphaelSolveCoordinator] Failed to load solution cache: {ex.Message}");
        }
    }

    public int PendingSolves => _inProgressTasks.Count + GetQueuedSolveCount();
    public int ActiveSolves => _activeSolveCount;
    public int CachedSolutionCount => _cachedSolutions.Count;

    public bool EnqueueOrPromoteRequest(RaphaelSolveRequest request, RaphaelSolvePriority priority = RaphaelSolvePriority.Urgent)
    {
        if (!_config.RaphaelEnabled)
        {
            return false;
        }

        var (enqueued, promoted) = EnqueuePreparedRequests(new[] { request }, priority);
        if (enqueued + promoted <= 0)
        {
            return false;
        }

        ProcessPendingQueue();
        return true;
    }

    public void EnqueueSolvesFromRequests(IEnumerable<RaphaelSolveRequest> requests, RaphaelSolvePriority priority = RaphaelSolvePriority.Urgent)
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
        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Enqueuing {uniqueCrafts.Count} unique crafts for Raphael solving at {priority} priority (max concurrent: {_config.MaxConcurrentRaphaelProcesses})");
        var (enqueued, promoted) = EnqueuePreparedRequests(uniqueCrafts.Values, priority);
        var (urgentQueued, backgroundQueued) = GetQueuedCounts();
        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Queue prepared: {urgentQueued} urgent, {backgroundQueued} background, {_inProgressTasks.Count} in progress, {_cachedSolutions.Count} cached, {enqueued} enqueued, {promoted} promoted");
        ProcessPendingQueue();
    }

    public void EnqueueSolvesForJobs(IEnumerable<CraftingListItem> queue, Dictionary<uint, GameStateBuilder.PlayerStats> jobStatsMap, RaphaelSolvePriority priority = RaphaelSolvePriority.Urgent)
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
        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Enqueuing {uniqueCrafts.Count} unique crafts for Raphael solving at {priority} priority (max concurrent: {_config.MaxConcurrentRaphaelProcesses})");
        var (enqueued, promoted) = EnqueuePreparedRequests(uniqueCrafts.Values, priority);
        var (urgentQueued, backgroundQueued) = GetQueuedCounts();
        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Queue prepared: {urgentQueued} urgent, {backgroundQueued} background, {_inProgressTasks.Count} in progress, {_cachedSolutions.Count} cached, {enqueued} enqueued, {promoted} promoted");
        ProcessPendingQueue();
    }

    public void EnqueueSolvesFromCraftStates(IEnumerable<CraftingListItem> queue, List<(uint RecipeId, int Craftsmanship, int Control, int CP, int Level, bool Manipulation, bool Specialist)> recipeStats, RaphaelSolvePriority priority = RaphaelSolvePriority.Urgent)
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
            {
                continue;
            }

            var recipe = RecipeManager.GetRecipe(item.RecipeId);
            if (recipe == null)
            {
                continue;
            }

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
        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Enqueuing {uniqueCrafts.Count} unique crafts for Raphael solving at {priority} priority (max concurrent: {_config.MaxConcurrentRaphaelProcesses})");
        var (enqueued, promoted) = EnqueuePreparedRequests(uniqueCrafts.Values, priority);
        var (urgentQueued, backgroundQueued) = GetQueuedCounts();
        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Queue prepared: {urgentQueued} urgent, {backgroundQueued} background, {_inProgressTasks.Count} in progress, {_cachedSolutions.Count} cached, {enqueued} enqueued, {promoted} promoted");
        ProcessPendingQueue();
    }

    public void EnqueueSolves(IEnumerable<CraftingListItem> queue, int playerCraftsmanship, int playerControl, int playerCP, int playerLevel, bool manipulationUnlocked, bool isSpecialist, RaphaelSolvePriority priority = RaphaelSolvePriority.Urgent)
    {
        if (!_config.RaphaelEnabled)
        {
            GatherBuddy.Log.Debug("[RaphaelSolveCoordinator] Raphael solver disabled, skipping enqueue");
            return;
        }

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Starting Raphael enqueue: Craftsmanship={playerCraftsmanship}, Control={playerControl}, CP={playerCP}, Level={playerLevel}, Manipulation={manipulationUnlocked}, Specialist={isSpecialist}");

        var uniqueCrafts = ExtractUniqueCrafts(queue, playerCraftsmanship, playerControl, playerCP, playerLevel, manipulationUnlocked, isSpecialist);
        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Extracted {uniqueCrafts.Count} unique crafts from queue of {queue.Count()} items");

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Enqueuing {uniqueCrafts.Count} unique crafts for Raphael solving at {priority} priority (max concurrent: {_config.MaxConcurrentRaphaelProcesses})");
        var (enqueued, promoted) = EnqueuePreparedRequests(uniqueCrafts, priority);
        var (urgentQueued, backgroundQueued) = GetQueuedCounts();
        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Queue prepared: {urgentQueued} urgent, {backgroundQueued} background, {_inProgressTasks.Count} in progress, {_cachedSolutions.Count} cached, {enqueued} enqueued, {promoted} promoted");
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
        lock (_queueLock)
        {
            return _cachedSolutions.ContainsKey(key)
                || _inProgressTasks.ContainsKey(key)
                || ContainsQueuedRequestLocked(key);
        }
    }

    public void ClearIfAutoEnabled()
    {
        if (!_config.AutoClearSolutionCache)
        {
            return;
        }

        GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Auto-clearing solution cache on queue start ({_cachedSolutions.Count} solutions)");
        _cachedSolutions.Clear();
    }

    public void ReenqueueIfMissing(RaphaelSolveRequest request)
    {
        if (!_config.RaphaelEnabled || IsKnown(request))
        {
            return;
        }

        GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Re-enqueueing missing solution for recipe {request.RecipeId}");
        EnqueueOrPromoteRequest(request, RaphaelSolvePriority.Urgent);
    }

    public void Clear()
    {
        CancelAllPendingSolves();
        _cachedSolutions.Clear();
        Save();
    }

    public void CancelAllPendingSolves()
    {
        Queue<RaphaelSolveRequest> cancelledUrgent;
        Queue<RaphaelSolveRequest> cancelledBackground;
        lock (_queueLock)
        {
            cancelledUrgent = _urgentQueue;
            cancelledBackground = _backgroundQueue;
            _urgentQueue = new();
            _backgroundQueue = new();
        }

        var cancelledQueuedCount = cancelledUrgent.Count + cancelledBackground.Count;
        var cancelledActiveCount = 0;
        foreach (var task in _inProgressTasks.Values)
        {
            task.UserCancelled = true;
            task.CTS.Cancel();
            cancelledActiveCount++;
        }

        GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Cancelled Raphael queue: {cancelledQueuedCount} queued, {cancelledActiveCount} active");
        if (cancelledQueuedCount > 0 || cancelledActiveCount > 0)
        {
            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Queue stop requested while {_cachedSolutions.Count} cached solutions remain available");
        }
    }

    public bool RemoveCachedSolution(RaphaelSolveRequest request)
    {
        var key = request.GetKey();
        if (_cachedSolutions.TryRemove(key, out _))
        {
            Save();
            return true;
        }
        return false;
    }

    private int GetQueuedSolveCount()
    {
        lock (_queueLock)
        {
            return _urgentQueue.Count + _backgroundQueue.Count;
        }
    }

    private (int Urgent, int Background) GetQueuedCounts()
    {
        lock (_queueLock)
        {
            return (_urgentQueue.Count, _backgroundQueue.Count);
        }
    }

    private (int Enqueued, int Promoted) EnqueuePreparedRequests(IEnumerable<RaphaelSolveRequest> requests, RaphaelSolvePriority priority)
    {
        var enqueued = 0;
        var promoted = 0;
        lock (_queueLock)
        {
            foreach (var request in requests)
            {
                var key = request.GetKey();
                if (_cachedSolutions.ContainsKey(key))
                {
                    GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Recipe {request.RecipeId} already cached");
                    continue;
                }

                if (_inProgressTasks.ContainsKey(key))
                {
                    GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Recipe {request.RecipeId} already in progress");
                    continue;
                }

                if (priority == RaphaelSolvePriority.Urgent && TryRemoveQueuedRequestLocked(ref _backgroundQueue, key))
                {
                    _urgentQueue.Enqueue(request);
                    promoted++;
                    GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Promoted recipe {request.RecipeId} to urgent priority (key: {key})");
                    continue;
                }

                if (ContainsQueuedRequestLocked(key))
                {
                    GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Recipe {request.RecipeId} already queued");
                    continue;
                }

                GetQueueForPriorityLocked(priority).Enqueue(request);
                enqueued++;
                GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Queued recipe {request.RecipeId} for solving at {priority} priority (key: {key})");
            }
        }

        return (enqueued, promoted);
    }

    private Queue<RaphaelSolveRequest> GetQueueForPriorityLocked(RaphaelSolvePriority priority)
        => priority == RaphaelSolvePriority.Urgent
            ? _urgentQueue
            : _backgroundQueue;

    private bool ContainsQueuedRequestLocked(string key)
        => _urgentQueue.Any(request => request.GetKey() == key)
            || _backgroundQueue.Any(request => request.GetKey() == key);

    private static bool TryRemoveQueuedRequestLocked(ref Queue<RaphaelSolveRequest> queue, string key)
    {
        if (queue.Count == 0)
        {
            return false;
        }

        var removed = false;
        var retained = new Queue<RaphaelSolveRequest>(queue.Count);
        while (queue.Count > 0)
        {
            var queuedRequest = queue.Dequeue();
            if (!removed && queuedRequest.GetKey() == key)
            {
                removed = true;
                continue;
            }

            retained.Enqueue(queuedRequest);
        }

        queue = retained;
        return removed;
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

        return [.. uniqueCrafts.Values];
    }

    private static int CalculateInitialQuality(CraftingListItem item, Recipe recipe)
    {
        item.QualityPolicy ??= CraftingQualityPolicyResolver.Resolve(recipe, item.CraftSettings);
        if (item.IngredientPreferences.Count == 0)
        {
            item.IngredientPreferences = item.QualityPolicy.BuildGuaranteedHQPreferences();
        }

        return item.QualityPolicy.CalculateGuaranteedInitialQuality(recipe);
    }

    private void ProcessPendingQueue()
    {
        while (_activeSolveCount < _config.MaxConcurrentRaphaelProcesses)
        {
            RaphaelSolveRequest? request = null;
            RaphaelSolvePriority? priority = null;
            lock (_queueLock)
            {
                if (_urgentQueue.Count > 0)
                {
                    request = _urgentQueue.Dequeue();
                    priority = RaphaelSolvePriority.Urgent;
                }
                else if (_backgroundQueue.Count > 0)
                {
                    request = _backgroundQueue.Dequeue();
                    priority = RaphaelSolvePriority.Background;
                }
            }

            if (request == null || priority == null)
            {
                break;
            }

            var (urgentQueued, backgroundQueued) = GetQueuedCounts();
            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Processing {priority} queue: {urgentQueued} urgent, {backgroundQueued} background remaining, {_activeSolveCount}/{_config.MaxConcurrentRaphaelProcesses} active");
            _ = SpawnRaphaelSolveAsync(request);
        }

        var (remainingUrgent, remainingBackground) = GetQueuedCounts();
        if (remainingUrgent + remainingBackground > 0)
        {
            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Queue processing paused: {remainingUrgent} urgent, {remainingBackground} background, {_activeSolveCount}/{_config.MaxConcurrentRaphaelProcesses} active");
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

        Interlocked.Increment(ref _activeSolveCount);
        var task = Task.Run(async () =>
        {
            try
            {
                await ExecuteRaphaelSolve(request, cts.Token);
            }
            finally
            {
                Interlocked.Decrement(ref _activeSolveCount);
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
        Process? process = null;
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
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

            process = new Process
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
            outputTask = process.StandardOutput.ReadToEndAsync();
            errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            GatherBuddy.Log.Debug($"[RaphaelSolveCoordinator] Raphael process output received ({output.Length} bytes stdout, {error.Length} bytes stderr)");
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
        catch (OperationCanceledException) when (_inProgressTasks.TryGetValue(key, out var solveTask) && solveTask.UserCancelled)
        {
            TryKillRaphaelProcess(process, request.RecipeId, "user cancellation");
            await DrainProcessOutputAsync(outputTask, errorTask).ConfigureAwait(false);
            GatherBuddy.Log.Information($"[RaphaelSolveCoordinator] Cancelled Raphael solve for recipe {request.RecipeId}");
        }
        catch (OperationCanceledException)
        {
            TryKillRaphaelProcess(process, request.RecipeId, "timeout");
            await DrainProcessOutputAsync(outputTask, errorTask).ConfigureAwait(false);
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
        finally
        {
            process?.Dispose();
        }
    }

    private string BuildRaphaelArguments(RaphaelSolveRequest request)
    {
        var args = new StringBuilder();
        args.Append($"solve --recipe-id {request.RecipeId} ");
        args.Append($"--level {request.Level} ");
        args.Append($"--stats {request.Craftsmanship} {request.Control} {request.CP} ");

        if (request.Manipulation)
        {
            args.Append("--manipulation ");
        }

        if (request.InitialQuality > 0)
        {
            args.Append($"--initial {request.InitialQuality} ");
        }

        if (_config.RaphaelBackloadProgress)
        {
            args.Append("--backload-progress ");
        }

        if (_config.RaphaelAllowSpecialistActions && request.Specialist)
        {
            args.Append("--heart-and-soul --quick-innovation ");
        }

        args.Append("--output-variables action_ids");

        return args.ToString();
    }

    private List<uint> ParseRaphaelOutput(string output)
    {
        var actionIds = new List<uint>();

        if (string.IsNullOrWhiteSpace(output))
        {
            return actionIds;
        }

        try
        {
            var cleaned = output.Replace("[", "").Replace("]", "").Replace("\"", "").Trim();
            var parts = cleaned.Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                if (uint.TryParse(part.Trim(), out var actionId))
                {
                    actionIds.Add(actionId);
                }
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
            {
                return string.Empty;
            }

            return Path.Combine(pluginDir, "raphael-cli.exe");
        }
        catch
        {
            return string.Empty;
        }
    }
}
