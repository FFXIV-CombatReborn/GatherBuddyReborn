using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.AutoGather;

public class TaskManager : IDisposable
{
    private readonly IFramework _framework;
    private readonly Dictionary<string, long> _throttlers = [];

    private readonly List<TaskItem> _tasks = [];
    private readonly List<TaskItem> _immediateTasks = [];
    private TaskItem? _currentTask;
    private long _abortAt;

    public int MaxTasks { get; private set; }
    public int TimeLimitMS { get; set; } = 10000;
    public bool AbortOnTimeout { get; set; } = false;
    public bool TimeoutSilently { get; set; } = false;
    public bool ShowDebug { get; set; } = true;

    public string? CurrentTaskName => _currentTask?.Name;
    public List<string?> TaskStack => [.. _immediateTasks.Select(x => x.Name).Union(_tasks.Select(x => x.Name))];
    public int NumQueuedTasks => _tasks.Count + _immediateTasks.Count + (_currentTask == null ? 0 : 1);
    public bool IsBusy => _currentTask != null || _tasks.Count > 0 || _immediateTasks.Count > 0;

    public TaskManager(IFramework framework)
    {
        _framework = framework;
        _framework.Update += Tick;
    }

    public void Dispose()
    {
        _framework.Update -= Tick;
        Abort();
    }

    public void Abort()
    {
        _tasks.Clear();
        _immediateTasks.Clear();
        _currentTask = null;
        MaxTasks = 0;
        _throttlers.Clear();
    }

    public void SetStepMode(bool enabled)
    {
        _framework.Update -= Tick;
        if (!enabled)
        {
            _framework.Update += Tick;
        }
    }

    public void Step() => Tick(null!);

    #region Enqueue

    public void Enqueue(Func<bool?> task, string? name = null)
    {
        _tasks.Add(new TaskItem(task, TimeLimitMS, AbortOnTimeout, name));
        MaxTasks++;
    }

    public void Enqueue(Func<bool?> task, int timeLimitMs, string? name = null)
    {
        _tasks.Add(new TaskItem(task, timeLimitMs, AbortOnTimeout, name));
        MaxTasks++;
    }

    public void Enqueue(Func<bool?> task, bool abortOnTimeout, string? name = null)
    {
        _tasks.Add(new TaskItem(task, TimeLimitMS, abortOnTimeout, name));
        MaxTasks++;
    }

    public void Enqueue(Func<bool?> task, int timeLimitMs, bool abortOnTimeout, string? name = null)
    {
        _tasks.Add(new TaskItem(task, timeLimitMs, abortOnTimeout, name));
        MaxTasks++;
    }

    public void Enqueue(Action task, string? name = null)
    {
        _tasks.Add(new TaskItem(() => { task(); return true; }, TimeLimitMS, AbortOnTimeout, name));
        MaxTasks++;
    }

    public void Enqueue(Action task, int timeLimitMs, string? name = null)
    {
        _tasks.Add(new TaskItem(() => { task(); return true; }, timeLimitMs, AbortOnTimeout, name));
        MaxTasks++;
    }

    public void Enqueue(Action task, bool abortOnTimeout, string? name = null)
    {
        _tasks.Add(new TaskItem(() => { task(); return true; }, TimeLimitMS, abortOnTimeout, name));
        MaxTasks++;
    }

    public void Enqueue(Action task, int timeLimitMs, bool abortOnTimeout, string? name = null)
    {
        _tasks.Add(new TaskItem(() => { task(); return true; }, timeLimitMs, abortOnTimeout, name));
        MaxTasks++;
    }

    #endregion

    #region EnqueueImmediate

    public void EnqueueImmediate(Func<bool?> task, string? name = null)
    {
        _immediateTasks.Add(new TaskItem(task, TimeLimitMS, AbortOnTimeout, name));
        MaxTasks++;
    }

    public void EnqueueImmediate(Func<bool?> task, int timeLimitMs, string? name = null)
    {
        _immediateTasks.Add(new TaskItem(task, timeLimitMs, AbortOnTimeout, name));
        MaxTasks++;
    }

    public void EnqueueImmediate(Func<bool?> task, bool abortOnTimeout, string? name = null)
    {
        _immediateTasks.Add(new TaskItem(task, TimeLimitMS, abortOnTimeout, name));
        MaxTasks++;
    }

    public void EnqueueImmediate(Func<bool?> task, int timeLimitMs, bool abortOnTimeout, string? name = null)
    {
        _immediateTasks.Add(new TaskItem(task, timeLimitMs, abortOnTimeout, name));
        MaxTasks++;
    }

    public void EnqueueImmediate(Action task, string? name = null)
    {
        _immediateTasks.Add(new TaskItem(() => { task(); return true; }, TimeLimitMS, AbortOnTimeout, name));
        MaxTasks++;
    }

    public void EnqueueImmediate(Action task, int timeLimitMs, string? name = null)
    {
        _immediateTasks.Add(new TaskItem(() => { task(); return true; }, timeLimitMs, AbortOnTimeout, name));
        MaxTasks++;
    }

    public void EnqueueImmediate(Action task, bool abortOnTimeout, string? name = null)
    {
        _immediateTasks.Add(new TaskItem(() => { task(); return true; }, TimeLimitMS, abortOnTimeout, name));
        MaxTasks++;
    }

    public void EnqueueImmediate(Action task, int timeLimitMs, bool abortOnTimeout, string? name = null)
    {
        _immediateTasks.Add(new TaskItem(() => { task(); return true; }, timeLimitMs, abortOnTimeout, name));
        MaxTasks++;
    }

    #endregion

    #region Insert

    public void Insert(Func<bool?> task, string? name = null)
    {
        _tasks.Insert(0, new TaskItem(task, TimeLimitMS, AbortOnTimeout, name));
        MaxTasks++;
    }

    public void Insert(Func<bool?> task, int timeLimitMs, string? name = null)
    {
        _tasks.Insert(0, new TaskItem(task, timeLimitMs, AbortOnTimeout, name));
        MaxTasks++;
    }

    public void Insert(Func<bool?> task, bool abortOnTimeout, string? name = null)
    {
        _tasks.Insert(0, new TaskItem(task, TimeLimitMS, abortOnTimeout, name));
        MaxTasks++;
    }

    public void Insert(Func<bool?> task, int timeLimitMs, bool abortOnTimeout, string? name = null)
    {
        _tasks.Insert(0, new TaskItem(task, timeLimitMs, abortOnTimeout, name));
        MaxTasks++;
    }

    public void Insert(Action task, string? name = null)
    {
        _tasks.Insert(0, new TaskItem(() => { task(); return true; }, TimeLimitMS, AbortOnTimeout, name));
        MaxTasks++;
    }

    public void Insert(Action task, int timeLimitMs, string? name = null)
    {
        _tasks.Insert(0, new TaskItem(() => { task(); return true; }, timeLimitMs, AbortOnTimeout, name));
        MaxTasks++;
    }

    public void Insert(Action task, bool abortOnTimeout, string? name = null)
    {
        _tasks.Insert(0, new TaskItem(() => { task(); return true; }, TimeLimitMS, abortOnTimeout, name));
        MaxTasks++;
    }

    public void Insert(Action task, int timeLimitMs, bool abortOnTimeout, string? name = null)
    {
        _tasks.Insert(0, new TaskItem(() => { task(); return true; }, timeLimitMs, abortOnTimeout, name));
        MaxTasks++;
    }

    #endregion

    #region Delay Helpers

    public void DelayNext(int delayMS) => DelayNext("GatherBuddyGenericDelay", delayMS);

    public void DelayNext(string uniqueName, int delayMS)
    {
        Enqueue(() => Throttle(uniqueName, delayMS), $"Throttle({uniqueName}, {delayMS})");
        Enqueue(() => CheckThrottle(uniqueName), $"CheckThrottle({uniqueName})");
        MaxTasks += 2;
    }

    public void DelayNextImmediate(int delayMS) => DelayNextImmediate("GatherBuddyGenericDelay", delayMS);

    public void DelayNextImmediate(string uniqueName, int delayMS)
    {
        EnqueueImmediate(() => Throttle(uniqueName, delayMS), $"Throttle({uniqueName}, {delayMS})");
        EnqueueImmediate(() => CheckThrottle(uniqueName), $"CheckThrottle({uniqueName})");
        MaxTasks += 2;
    }

    public void InsertDelayNext(int delayMS) => InsertDelayNext("GatherBuddyGenericDelay", delayMS);

    public void InsertDelayNext(string uniqueName, int delayMS)
    {
        Insert(() => CheckThrottle(uniqueName), $"CheckThrottle({uniqueName})");
        Insert(() => Throttle(uniqueName, delayMS), $"Throttle({uniqueName}, {delayMS})");
        MaxTasks += 2;
    }

    private bool Throttle(string name, int delayMS)
    {
        _throttlers[name] = Environment.TickCount64 + delayMS;
        return true;
    }

    private bool CheckThrottle(string name)
    {
        if (!_throttlers.TryGetValue(name, out var time))
        {
            return true;
        }

        if (Environment.TickCount64 >= time)
        {
            _throttlers.Remove(name);
            return true;
        }

        return false;
    }

    #endregion

    private void Tick(IFramework _)
    {
        if (_currentTask == null)
        {
            if (_immediateTasks.Count > 0)
            {
                _currentTask = _immediateTasks[0];
                _immediateTasks.RemoveAt(0);

                if (ShowDebug)
                {
                    GatherBuddy.Log.Debug($"[TaskManager] Starting immediate task: {_currentTask.Name ?? "unnamed"}");
                }

                _abortAt = Environment.TickCount64 + _currentTask.TimeLimitMS;
            }
            else if (_tasks.Count > 0)
            {
                _currentTask = _tasks[0];
                _tasks.RemoveAt(0);

                if (ShowDebug)
                {
                    GatherBuddy.Log.Debug($"[TaskManager] Starting task: {_currentTask.Name ?? "unnamed"}");
                }

                _abortAt = Environment.TickCount64 + _currentTask.TimeLimitMS;
            }
            else
            {
                MaxTasks = 0;
            }
        }
        else
        {
            try
            {
                var result = _currentTask.Action();

                if (result == true)
                {
                    if (ShowDebug)
                    {
                        GatherBuddy.Log.Debug($"[TaskManager] Task completed: {_currentTask.Name ?? "unnamed"}");
                    }

                    _currentTask = null;
                }
                else if (result == false)
                {
                    if (Environment.TickCount64 > _abortAt)
                    {
                        if (_currentTask.AbortOnTimeout)
                        {
                            var logMessage = $"[TaskManager] Clearing {_tasks.Count} remaining tasks due to timeout";
                            if (TimeoutSilently)
                            {
                                GatherBuddy.Log.Verbose(logMessage);
                            }
                            else
                            {
                                GatherBuddy.Log.Warning(logMessage);
                            }

                            _tasks.Clear();
                            _immediateTasks.Clear();
                        }

                        throw new TimeoutException($"Task '{_currentTask.Name ?? "unnamed"}' took too long to execute");
                    }
                }
                else
                {
                    GatherBuddy.Log.Warning($"[TaskManager] Aborting - task '{_currentTask.Name ?? "unnamed"}' returned null");
                    Abort();
                }
            }
            catch (TimeoutException e)
            {
                if (TimeoutSilently)
                {
                    GatherBuddy.Log.Verbose($"[TaskManager] {e.Message}");
                }
                else
                {
                    GatherBuddy.Log.Warning($"[TaskManager] {e.Message}");
                }

                _currentTask = null;
            }
            catch (Exception e)
            {
                GatherBuddy.Log.Error($"[TaskManager] Exception in task '{_currentTask?.Name ?? "unnamed"}': {e}");
                _currentTask = null;
            }
        }
    }

    private record TaskItem(Func<bool?> Action, int TimeLimitMS, bool AbortOnTimeout, string? Name);
}
