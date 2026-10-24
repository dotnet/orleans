using System.Collections.Concurrent;
using Orleans.Runtime.Diagnostics;

namespace UnitTests.GrainDirectory;

internal sealed class DirectoryChaosMonitor : IObserver<GrainDirectoryEvents.GrainDirectoryEvent>, IDisposable
{
    private readonly ConcurrentDictionary<SiloAddress, byte> _silos = new();
    private readonly ConcurrentQueue<string> _history = new();
    private readonly TaskCompletionSource<DirectoryChaosFailure> _invariantFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IDisposable _subscription;
    private long _successfulBatches;
    private long _expectedDisruptions;
    private string _lastExpectedDisruption = "none";

    public DirectoryChaosMonitor() => _subscription = GrainDirectoryEvents.AllEvents.Subscribe(this);

    public Task<DirectoryChaosFailure> InvariantFailure => _invariantFailure.Task;
    public long SuccessfulBatches => Interlocked.Read(ref _successfulBatches);
    public long ExpectedDisruptions => Interlocked.Read(ref _expectedDisruptions);

    public void TrackSilo(SiloAddress silo) => _silos.TryAdd(silo, 0);

    public void RecordPhase(string phase)
    {
        _history.Enqueue(phase);
        while (_history.Count > 64)
        {
            _history.TryDequeue(out _);
        }
    }

    public void RecordSuccessfulBatch() => Interlocked.Increment(ref _successfulBatches);

    public void RecordExpectedDisruption(Exception exception)
    {
        Interlocked.Increment(ref _expectedDisruptions);
        var summary = exception is AggregateException aggregate
            ? string.Join(", ", aggregate.Flatten().InnerExceptions
                .GroupBy(error => error.GetType().Name)
                .Select(group => $"{group.Key}: {group.Count()}"))
            : exception.GetType().Name;
        Volatile.Write(ref _lastExpectedDisruption, summary);
    }

    public DirectoryChaosFailure RuntimeFailure(string phase, Exception exception) =>
        new(false, phase, CaptureHistory(), exception);

    public void OnNext(GrainDirectoryEvents.GrainDirectoryEvent value)
    {
        if (value is GrainDirectoryEvents.IntegrityViolation violation && _silos.ContainsKey(value.SiloAddress))
        {
            _invariantFailure.TrySetResult(new(
                true,
                $"silo={value.SiloAddress}, partition={value.PartitionIndex}, view={value.Version}, range={value.Range}, grain={violation.GrainId}",
                CaptureHistory(),
                violation.Exception));
        }
    }

    public void OnError(Exception error) =>
        _invariantFailure.TrySetResult(RuntimeFailure("directory diagnostic stream", error));

    public void OnCompleted()
    {
    }

    public void Dispose() => _subscription.Dispose();

    public static bool IsExpectedDisruption(Exception exception) => exception switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0
            && aggregate.InnerExceptions.All(IsExpectedDisruption),
        SiloUnavailableException or OrleansMessageRejectionException => true,
        _ => false,
    };

    public static bool IsExpectedShutdown(Exception exception) => exception switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0
            && aggregate.InnerExceptions.All(IsExpectedShutdown),
        OperationCanceledException => true,
        _ => IsExpectedDisruption(exception),
    };

    private string CaptureHistory() =>
        $"Successful workload batches: {SuccessfulBatches}; expected disruptions: {ExpectedDisruptions}.{Environment.NewLine}"
        + $"Last expected disruption: {Volatile.Read(ref _lastExpectedDisruption)}.{Environment.NewLine}"
        + string.Join(Environment.NewLine, _history);
}

internal sealed class DirectoryChaosFailure(
    bool hasInvariantEvidence,
    string phase,
    string history,
    Exception innerException) : Exception(
        $"{(hasInvariantEvidence ? "Directory invariant violation" : "Unclassified runtime/infrastructure failure")} during {phase}."
        + $"{Environment.NewLine}{history}",
        innerException)
{
    public bool HasInvariantEvidence { get; } = hasInvariantEvidence;
    public string Phase { get; } = phase;
}
