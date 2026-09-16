namespace Orleans.Dissemination.PerformanceHarness;

internal sealed record OpenLoopPlan(DateTimeOffset StartUtc, int DurationSeconds, int ProducerIndex, int ProducerCount, string Workload)
{
    public void Validate()
    {
        if (DurationSeconds is < 3 or > 30 || ProducerCount is < 3 or > 32
            || ProducerIndex < 0 || ProducerIndex >= ProducerCount
            || Workload is not ("OpenLoopSynchronized" or "OpenLoopStaggered"))
        {
            throw new InvalidOperationException("Open-loop requires 3..32 producers, 3..30 seconds, and a synchronized or staggered pattern.");
        }
    }

    public TimeSpan Offset(int sequence) =>
        TimeSpan.FromSeconds(sequence) + (Workload == "OpenLoopStaggered"
            ? TimeSpan.FromTicks(TimeSpan.TicksPerSecond * ProducerIndex / ProducerCount)
            : TimeSpan.Zero);
}

internal sealed record ProducedValue(DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, long Version, string Value);
internal sealed record OpenLoopPublication(int Sequence, DateTimeOffset ScheduledAtUtc, ProducedValue Result);
internal sealed record OpenLoopReport(OpenLoopPlan Plan, OpenLoopPublication[] Publications, int MissedPeriods, int Overruns);
internal sealed record OpenLoopProgress(int PublishedCount, bool Completed, int MissedPeriods, int Overruns, string? Error);

internal sealed class OpenLoopProducer(TimeProvider timeProvider) : IAsyncDisposable
{
    private CancellationTokenSource? _cancellation;
    private Task<OpenLoopReport>? _completion;
    private int _published;
    private int _missedPeriods;
    private int _overruns;
    private OpenLoopPlan? _plan;
    private readonly List<OpenLoopPublication> _publications = [];
    private readonly object _lock = new();

    public Task<OpenLoopReport> Completion => _completion
        ?? throw new InvalidOperationException("The open-loop producer has not been armed.");

    public OpenLoopProgress? Progress => _completion is { } task
        ? new(Volatile.Read(ref _published), task.IsCompletedSuccessfully, Volatile.Read(ref _missedPeriods),
            Volatile.Read(ref _overruns), task.Exception?.GetBaseException().ToString())
        : null;

    public OpenLoopReport Report
    {
        get
        {
            lock (_lock)
            {
                return new(_plan ?? throw new InvalidOperationException("The open-loop producer has not been armed."),
                    _publications.ToArray(), _missedPeriods, _overruns);
            }
        }
    }

    public void Start(OpenLoopPlan plan, Func<CancellationToken, Task<ProducedValue>> publish, CancellationToken cancellationToken)
    {
        if (_completion is not null)
        {
            throw new InvalidOperationException("The open-loop producer has already been armed.");
        }

        plan.Validate();
        var lead = plan.StartUtc - timeProvider.GetUtcNow();
        if (lead <= TimeSpan.Zero || lead > TimeSpan.FromSeconds(10))
        {
            throw new InvalidOperationException("Open-loop start must be in the next 10 seconds; all workers must acknowledge arming before it.");
        }

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _plan = plan;
        _completion = Run(plan, lead, publish, _cancellation.Token);
        _ = _completion.ContinueWith(
            static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<OpenLoopReport> Run(
        OpenLoopPlan plan, TimeSpan lead, Func<CancellationToken, Task<ProducedValue>> publish, CancellationToken cancellationToken)
    {
        var origin = timeProvider.GetTimestamp();
        for (var sequence = 0; sequence < plan.DurationSeconds; sequence++)
        {
            var due = lead + plan.Offset(sequence);
            await DelayUntil(due);
            var elapsed = timeProvider.GetElapsedTime(origin);
            if (elapsed - due >= TimeSpan.FromSeconds(1))
            {
                Volatile.Write(ref _missedPeriods, (int)(elapsed - due).TotalSeconds);
                throw new InvalidOperationException($"Open-loop producer {plan.ProducerIndex} missed publication slot {sequence}.");
            }

            var result = await publish(cancellationToken);
            lock (_lock)
            {
                if (_publications.Count > 0 && result.Version <= _publications[^1].Result.Version)
                {
                    throw new InvalidOperationException("The production publisher did not create a strictly newer sample.");
                }
                _publications.Add(new(sequence, plan.StartUtc + plan.Offset(sequence), result));
                Volatile.Write(ref _published, _publications.Count);
            }
            if (timeProvider.GetElapsedTime(origin) - due >= TimeSpan.FromSeconds(1))
            {
                Volatile.Write(ref _overruns, 1);
                throw new InvalidOperationException($"Open-loop producer {plan.ProducerIndex} publication {sequence} exceeded its 1 Hz slot.");
            }
        }

        await DelayUntil(lead + TimeSpan.FromSeconds(plan.DurationSeconds));
        return Report;

        async Task DelayUntil(TimeSpan due)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = due - timeProvider.GetElapsedTime(origin);
                if (remaining <= TimeSpan.Zero)
                {
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), timeProvider, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cancellation is { } cancellation)
        {
            await cancellation.CancelAsync();
            try
            {
                await Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                cancellation.Dispose();
                _cancellation = null;
            }
        }
    }
}
