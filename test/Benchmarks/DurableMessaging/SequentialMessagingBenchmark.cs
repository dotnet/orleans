using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.DurableMessaging;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace Benchmarks.DurableMessaging;

/// <summary>
/// Measures one sequential chain of acknowledged durable messages through a small grain ring.
/// </summary>
[BenchmarkCategory("DurableMessaging")]
public class SequentialMessagingBenchmark
{
    private const int MessagesPerInvocation = 128;
    private InProcessTestCluster? _cluster;
    private CommittedHopProbe _probe = null!;
    private ISequentialMessagingGrain[] _grains = [];
    private int _completedRuns;

    /// <summary>Gets or sets the number of interacting grains on one silo.</summary>
    [Params(2, 4, 8)]
    public int GrainCount { get; set; }

    /// <summary>Creates fresh bounded storage and warms the ring outside measurement.</summary>
    [IterationSetup]
    public void SetupIteration() => SetupIterationAsync().GetAwaiter().GetResult();

    private async Task SetupIterationAsync()
    {
        _probe = new CommittedHopProbe();
        _completedRuns = 0;
        var builder = new InProcessTestClusterBuilder(1);
        builder.Options.ConfigureFileLogging = false;
        builder.ConfigureHost(host => host.Logging.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureSilo((_, silo) =>
        {
            silo.UseInMemoryDurableJobs();
            silo.AddVolatileJournalStorage();
            silo.AddDurableMessaging();
            silo.Services.AddSingleton(_probe);
        });
        _cluster = builder.Build();
        await _cluster.DeployAsync();
        _grains = Enumerable.Range(0, GrainCount)
            .Select(index => _cluster.Client.GetGrain<ISequentialMessagingGrain>(index))
            .ToArray();
        for (var index = 0; index < _grains.Length; index++)
        {
            await _grains[index].ConfigureAsync(_grains[(index + 1) % _grains.Length].GetGrainId());
        }
        await RunChainAsync();
    }

    /// <summary>Completes 128 sequential deliveries, normalized to one acknowledged message.</summary>
    /// <returns>The exact number of committed handler steps.</returns>
    [Benchmark(OperationsPerInvoke = MessagesPerInvocation)]
    public Task<int> SequentialRing() => RunChainAsync();

    private async Task<int> RunChainAsync()
    {
        var runId = Guid.NewGuid();
        var completion = _probe.Begin(runId, MessagesPerInvocation);
        await _grains[0].StartAsync(runId, MessagesPerInvocation);
        int count;
        try
        {
            count = await completion.WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Run {runId}, {GrainCount}-grain ring: awaiting journal acknowledgements, {_probe.AcknowledgedCount}/{MessagesPerInvocation} observed.",
                exception);
        }
        if (count != MessagesPerInvocation)
        {
            throw new InvalidOperationException($"Run {runId} committed {count} of {MessagesPerInvocation} messages.");
        }
        _completedRuns++;
        return count;
    }

    /// <summary>Checks business-effect counts and drains and disposes the cluster outside measurement.</summary>
    [IterationCleanup]
    public void CleanupIteration() => CleanupIterationAsync().GetAwaiter().GetResult();

    private async Task CleanupIterationAsync()
    {
        if (_cluster is not { } cluster)
        {
            return;
        }

        try
        {
            var counts = await Task.WhenAll(_grains.Select(grain => grain.GetProcessedCountAsync()));
            var expected = (long)_completedRuns * MessagesPerInvocation;
            if (counts.Sum() != expected || counts.Any(count => count != expected / GrainCount))
            {
                throw new InvalidOperationException(
                    $"The {GrainCount}-grain ring applied [{string.Join(", ", counts)}]; expected {expected} total effects.");
            }
        }
        finally
        {
            await cluster.DisposeAsync();
            _cluster = null;
        }
    }

    /// <summary>Disposes an owner left by a failed iteration setup.</summary>
    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_cluster is { } cluster)
        {
            await cluster.DisposeAsync();
            _cluster = null;
        }
    }
}

/// <summary>Configures and seeds a sequential durable-message ring.</summary>
public interface ISequentialMessagingGrain : IGrainWithIntegerKey, IDurableMessagingGrain
{
    /// <summary>Persists the next destination before measurement.</summary>
    Task ConfigureAsync(GrainId next);

    /// <summary>Commits the initial outgoing intent.</summary>
    Task StartAsync(Guid runId, int messageCount);

    /// <summary>Returns the accumulated number of applied handler effects.</summary>
    Task<long> GetProcessedCountAsync();
}

/// <summary>One message in a sequential chain.</summary>
[GenerateSerializer]
public sealed record SequentialMessage(
    [property: Id(0)] Guid RunId,
    [property: Id(1)] int Hop,
    [property: Id(2)] int MessageCount);

/// <summary>Runs each hop through the production inbox, outbox, journal, and durable-job pumps.</summary>
public sealed class SequentialMessagingGrain : Grain, ISequentialMessagingGrain, IInboxHandler<SequentialMessage>, IJournaledStateHook
{
    private readonly IDurableOutbox _outbox;
    private readonly IDurableStateManager _state;
    private readonly IDurableValue<GrainId> _next;
    private readonly IDurableValue<long> _processed;
    private readonly IDurableValue<SequentialMessage> _progress;
    private readonly CommittedHopProbe _probe;
    private readonly Orleans.Serialization.Session.SerializerSessionPool _sessions;
    private SequentialMessage? _captured;

    /// <summary>Constructs the ring participant and its acknowledgement observer.</summary>
    public SequentialMessagingGrain(
        IDurableInbox inbox,
        IDurableOutbox outbox,
        IDurableStateManager state,
        IJournaledStateManager journal,
        [FromKeyedServices("ring-next")] IDurableValue<GrainId> next,
        [FromKeyedServices("ring-processed")] IDurableValue<long> processed,
        [FromKeyedServices("ring-progress")] IDurableValue<SequentialMessage> progress,
        CommittedHopProbe probe,
        Orleans.Serialization.Session.SerializerSessionPool sessions)
    {
        _outbox = outbox;
        _state = state;
        _next = next;
        _processed = processed;
        _progress = progress;
        _probe = probe;
        _sessions = sessions;
        inbox.RegisterHandler("benchmark/hop", this);
        journal.Hooks.Add(this);
    }

    /// <inheritdoc/>
    public async Task ConfigureAsync(GrainId next)
    {
        _next.Value = next;
        await _state.WriteStateAsync();
    }

    /// <inheritdoc/>
    public async Task StartAsync(Guid runId, int messageCount)
    {
        var envelope = new DurableEnvelopeBuilder(_sessions, this.GetGrainId())
            .To(_next.Value, "benchmark/hop")
            .WithBody(new SequentialMessage(runId, 0, messageCount))
            .Build();
        _outbox.Send(envelope);
        await _state.WriteStateAsync();
    }

    /// <inheritdoc/>
    public Task<long> GetProcessedCountAsync() => Task.FromResult(_processed.Value);

    /// <inheritdoc/>
    public ValueTask HandleAsync(
        SequentialMessage? message, IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.RunId == Guid.Empty || message.Hop < 0 || message.Hop >= message.MessageCount)
        {
            throw new ArgumentException("The sequential message has an invalid run or hop.");
        }
        var nextCount = checked(_processed.Value + 1);
        DurableEnvelope? output = message.Hop + 1 < message.MessageCount
            ? context.CreateEnvelope()
                .To(_next.Value, "benchmark/hop")
                .WithBody(message with { Hop = message.Hop + 1 })
                .Build()
            : null;
        cancellationToken.ThrowIfCancellationRequested();

        _processed.Value = nextCount;
        _progress.Value = message;
        if (output is { } envelope)
        {
            context.Send(envelope);
        }
        context.Complete();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask BeforeOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        _captured = _progress.Value;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask AfterOperationAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        if (_captured is { } message)
        {
            _probe.OnCommitted(message);
        }
        _captured = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Observes every hop after actual journal acknowledgement.</summary>
public sealed class CommittedHopProbe
{
    private readonly object _lock = new();
    private readonly HashSet<int> _acknowledged = [];
    private TaskCompletionSource<int> _completion = null!;
    private Guid _runId;
    private int _messageCount;

    /// <summary>Gets the number of distinct journal acknowledgements for the active chain.</summary>
    public int AcknowledgedCount
    {
        get
        {
            lock (_lock)
            {
                return _acknowledged.Count;
            }
        }
    }

    /// <summary>Arms completion before the initial intent is sent.</summary>
    public Task<int> Begin(Guid runId, int messageCount)
    {
        lock (_lock)
        {
            _runId = runId;
            _messageCount = messageCount;
            _acknowledged.Clear();
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _completion.Task;
        }
    }

    /// <summary>Signals a completed chain only after every distinct hop is acknowledged.</summary>
    public void OnCommitted(SequentialMessage message)
    {
        lock (_lock)
        {
            if (message.RunId == _runId && _acknowledged.Add(message.Hop)
                && _acknowledged.Count == _messageCount)
            {
                _completion.SetResult(_acknowledged.Count);
            }
        }
    }
}
