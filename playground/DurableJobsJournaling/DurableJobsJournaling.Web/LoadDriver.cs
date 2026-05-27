using System.Collections.Concurrent;
using System.Diagnostics;
using DurableJobsJournaling.Abstractions;

namespace DurableJobsJournaling.Web;

public sealed class LoadDriver(IClusterClient client, WorkflowMetrics metrics, ILogger<LoadDriver> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Task> _inflight = [];
    private readonly object _settingsLock = new();
    private readonly SemaphoreSlim _wakeSignal = new(0);
    private readonly TokenBucket _tokenBucket = new();
    private LoadSettings _settings = LoadSettings.Default;
    private bool _enabled;
    private DateTimeOffset? _startedAt;
    private long _started;
    private long _completed;
    private long _failed;

    public void Enable()
    {
        _enabled = true;
        _startedAt ??= DateTimeOffset.UtcNow;
        Wake();
    }

    public void Disable()
    {
        _enabled = false;
        Wake();
    }

    public LoadSettings UpdateSettings(LoadSettings settings)
    {
        var normalized = settings.Normalize();
        lock (_settingsLock)
        {
            _settings = normalized;
        }

        Wake();
        return normalized;
    }

    public LoadDriverState GetState()
    {
        LoadSettings settings;
        lock (_settingsLock)
        {
            settings = _settings;
        }

        return new LoadDriverState(
            _enabled,
            settings,
            _inflight.Count,
            Interlocked.Read(ref _started),
            Interlocked.Read(ref _completed),
            Interlocked.Read(ref _failed),
            _startedAt);
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        Disable();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inflight = _inflight.Values.ToArray();
            if (inflight.Length is 0)
            {
                return;
            }

            await Task.WhenAny(inflight).WaitAsync(cancellationToken);
        }
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await DrainAsync(cancellationToken);
        Interlocked.Exchange(ref _started, 0);
        Interlocked.Exchange(ref _completed, 0);
        Interlocked.Exchange(ref _failed, 0);
        _startedAt = null;
        _tokenBucket.Reset();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var state = GetState();
            if (state.Enabled && state.Settings.TargetConcurrency > 0)
            {
                _tokenBucket.Refill(state.Settings.TargetStartsPerSecond);
                while (_inflight.Count < state.Settings.TargetConcurrency && _tokenBucket.TryConsume(state.Settings.TargetStartsPerSecond))
                {
                    StartWorkflow(state.Settings, stoppingToken);
                }

                if (_inflight.Count < state.Settings.TargetConcurrency && _tokenBucket.GetDelayUntilNextToken(state.Settings.TargetStartsPerSecond) is { } delay && delay > TimeSpan.Zero)
                {
                    await WaitForWakeOrDelayAsync(delay, stoppingToken);
                    continue;
                }
            }

            await WaitForWakeAsync(stoppingToken);
        }
    }

    private void StartWorkflow(LoadSettings settings, CancellationToken stoppingToken)
    {
        var workflowId = Guid.NewGuid();
        metrics.RecordStarted();
        Interlocked.Increment(ref _started);

        var tracker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inflight[workflowId] = tracker.Task;
        _ = RunWorkflowAsync(workflowId, settings, tracker, stoppingToken);
    }

    private async Task RunWorkflowAsync(
        Guid workflowId,
        LoadSettings settings,
        TaskCompletionSource tracker,
        CancellationToken stoppingToken)
    {
        try
        {
            var startedAt = DateTimeOffset.UtcNow;
            var grain = client.GetGrain<IWorkflowCoordinatorGrain>(workflowId);
            await grain.StartAsync(new WorkflowStartRequest(workflowId, startedAt, settings));
            var result = await grain.AwaitCompletionAsync().WaitAsync(stoppingToken);
            if (result.Status is WorkflowStatus.Completed)
            {
                metrics.RecordCompleted(result);
                Interlocked.Increment(ref _completed);
            }
            else
            {
                metrics.RecordFailed(result);
                Interlocked.Increment(ref _failed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Workflow {WorkflowId} failed in the load driver", workflowId);
            metrics.RecordFailed(workflowId, exception);
            Interlocked.Increment(ref _failed);
        }
        finally
        {
            tracker.TrySetResult();
            _inflight.TryRemove(workflowId, out _);
            Wake();
        }
    }

    private Task WaitForWakeAsync(CancellationToken cancellationToken) => _wakeSignal.WaitAsync(cancellationToken);

    private async Task WaitForWakeOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var wakeTask = _wakeSignal.WaitAsync(delayCancellation.Token);
        var delayTask = Task.Delay(delay, cancellationToken);
        var completedTask = await Task.WhenAny(wakeTask, delayTask);
        if (completedTask == delayTask)
        {
            await delayCancellation.CancelAsync();
            try
            {
                await wakeTask;
            }
            catch (OperationCanceledException) when (delayCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private void Wake()
        => _wakeSignal.Release();

    private sealed class TokenBucket
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private double _tokens;
        private TimeSpan _lastRefillElapsed;
        private bool _hasRefillElapsed;

        public bool TryConsume(double targetStartsPerSecond)
        {
            if (targetStartsPerSecond <= 0)
            {
                return true;
            }

            if (_tokens < 1)
            {
                return false;
            }

            _tokens -= 1;
            return true;
        }

        public void Refill(double targetStartsPerSecond)
        {
            if (targetStartsPerSecond <= 0)
            {
                return;
            }

            var elapsed = _stopwatch.Elapsed;
            if (!_hasRefillElapsed)
            {
                _lastRefillElapsed = elapsed;
                _hasRefillElapsed = true;
                _tokens = Math.Min(targetStartsPerSecond, 1);
                return;
            }

            var elapsedSeconds = (elapsed - _lastRefillElapsed).TotalSeconds;
            _lastRefillElapsed = elapsed;
            _tokens = Math.Min(Math.Max(1, targetStartsPerSecond), _tokens + elapsedSeconds * targetStartsPerSecond);
        }

        public TimeSpan GetDelayUntilNextToken(double targetStartsPerSecond)
        {
            if (targetStartsPerSecond <= 0 || _tokens >= 1)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds((1 - _tokens) / targetStartsPerSecond);
        }

        public void Reset()
        {
            _tokens = 0;
            _lastRefillElapsed = TimeSpan.Zero;
            _hasRefillElapsed = false;
            _stopwatch.Restart();
        }
    }
}
