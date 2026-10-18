using System.Buffers;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Durable.Journaling;
using Orleans.Durable.Tasks;
using Orleans.Durable.Tasks.Runtime;
using Orleans.Durable.Tasks.Testing;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>
/// Captures the histories of executions which one version of a workflow started, and replays them against later
/// versions with <see cref="DurableTaskReplayer"/>.
/// </summary>
[Collection(DurableTaskRecoveryCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskReplayerTests(DurableTaskReplayFixture fixture) : IClassFixture<DurableTaskReplayFixture>
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HistoryRoundTripsAndReplaysToTheRecordedWait()
    {
        var (history, root) = await CaptureAsync("round-trip", ReplayWorkflowVersion.Original);

        using var stream = new MemoryStream();
        await history.WriteAsync(stream, TestCancellation);
        stream.Position = 0;
        var copy = await DurableTaskHistory.ReadAsync(stream, TestCancellation);
        Assert.Equal(history.GrainId, copy.GrainId);
        Assert.Equal(history.JournalFormat, copy.JournalFormat);
        Assert.Equal(history.CapturedAt, copy.CapturedAt);

        var result = await ReplayAsync(copy, ReplayWorkflowVersion.Original);

        // The same code reaches every recorded operation and waits for the signal which the history doesn't hold.
        result.EnsureSucceeded();
        var execution = Assert.Single(result.Executions);
        Assert.Equal(root, execution.Id);
        AssertOutcome(DurableTaskReplayOutcome.Waiting, result, root);
        Assert.Equal([root.Child("reserve"), root.Child("ship")], execution.Reached);
        Assert.Empty(execution.Skipped);
        Assert.Equal([root.Child("$signal:approved")], execution.WaitingOn);
    }

    [Fact]
    public async Task GuardedChangeKeepsAnEarlierExecutionOnVersionZero()
    {
        var (history, root) = await CaptureAsync("guarded", ReplayWorkflowVersion.Original);

        var result = await ReplayAsync(history, ReplayWorkflowVersion.CarrierBooking);

        // The earlier run shipped through the warehouse, past the point where the check now stands.
        result.EnsureSucceeded();
        var execution = result[root];
        AssertOutcome(DurableTaskReplayOutcome.Waiting, result, root);
        Assert.Equal(0, execution.Versions["carrier-booking"]);
        Assert.Equal([root.Child("reserve"), root.Child("ship")], execution.Reached);
    }

    [Fact]
    public async Task UnguardedChangeFailsTheReplay()
    {
        var (history, root) = await CaptureAsync("unguarded", ReplayWorkflowVersion.Original);

        var result = await ReplayAsync(history, ReplayWorkflowVersion.UnguardedBooking);

        // The changed call requests another method under the recorded "ship".
        Assert.False(result.Succeeded);
        var failure = Assert.Throws<DurableTaskReplayException>(result.EnsureSucceeded);
        Assert.Same(result, failure.Result);
        Assert.Contains($"Execution '{root}'", failure.Message, StringComparison.Ordinal);

        // The runtime stalls a diverging execution and keeps its task pending for code which replays it.
        var execution = result[root];
        AssertOutcome(DurableTaskReplayOutcome.Stalled, result, root);
        Assert.Equal(DurableTaskAttemptCancellationReason.Diverged, execution.Stall!.Reason);
        Assert.IsType<DurableTaskDivergenceException>(execution.Stall.InnerException);
    }

    [Fact]
    public async Task RetiredVersionStallsAnEarlierExecution()
    {
        var (history, root) = await CaptureAsync("retired", ReplayWorkflowVersion.Original);

        var result = await ReplayAsync(history, ReplayWorkflowVersion.RetiredWarehouse);

        Assert.False(result.Succeeded);
        var execution = result[root];
        AssertOutcome(DurableTaskReplayOutcome.Stalled, result, root);
        Assert.Equal(DurableTaskAttemptCancellationReason.UnsupportedVersion, execution.Stall!.Reason);
        var unsupported = Assert.IsType<DurableTaskVersionException>(execution.Stall.InnerException);
        Assert.Equal(("carrier-booking", 0, 1, 1), (unsupported.Name, unsupported.Version, unsupported.MinSupported, unsupported.MaxSupported));
    }

    [Fact]
    public async Task ReplayStopsBeforeAnOperationWhichTheHistoryDoesNotRecord()
    {
        var (history, root) = await CaptureAsync("new-work", ReplayWorkflowVersion.Original);
        var audits = fixture.ReplayProbe.Audits;

        var result = await ReplayAsync(history, ReplayWorkflowVersion.Audited);

        // The effect which the change adds after the recorded operations doesn't run.
        result.EnsureSucceeded();
        var execution = result[root];
        AssertOutcome(DurableTaskReplayOutcome.ReachedNewOperation, result, root);
        Assert.Equal(root.Child("audit"), execution.NewOperation);
        Assert.Equal("a DurableTask.RunInline effect", execution.NewOperationDescription);
        Assert.Equal(audits, fixture.ReplayProbe.Audits);
    }

    [Fact]
    public async Task ChangeWhichEndsEarlierCompletesFromRecordedOutcomes()
    {
        var (history, root) = await CaptureAsync("completes", ReplayWorkflowVersion.Original);

        var result = await ReplayAsync(history, ReplayWorkflowVersion.Unapproved);

        // The body completes from the recorded results, and the replay reports its result without recording it.
        result.EnsureSucceeded();
        AssertOutcome(DurableTaskReplayOutcome.Completed, result, root);
        Assert.Equal("shipped completes", result[root].Response!.GetResult<string>());
    }

    [Fact]
    public async Task RecordedDelayKeepsTheReplayWaiting()
    {
        var (history, root) = await CaptureAsync("delay", ReplayWorkflowVersion.Paused);

        var result = await ReplayAsync(history, ReplayWorkflowVersion.Paused);

        // The delay is recorded and pending, so the replay waits for it.
        result.EnsureSucceeded();
        AssertOutcome(DurableTaskReplayOutcome.Waiting, result, root);
        Assert.Equal([root.Child("pause")], result[root].WaitingOn);
    }
    [Fact]
    public async Task HistoryReplaysInTheJournalFormatWhichRecordedIt()
    {
        var code = new ReplayWorkflowCode();
        var probe = new ReplayWorkflowProbe();
        var capture = new DurableTaskRecoveryFixture(
            DurableTaskRecoveryFixture.OrleansBinary,
            silo => silo.Services.AddSingleton(code).AddSingleton(probe));
        await capture.InitializeAsync();
        try
        {
            var (history, root) = await CaptureAsync(capture, code, probe, "binary", ReplayWorkflowVersion.Original);
            Assert.Equal(DurableTaskRecoveryFixture.OrleansBinary, history.JournalFormat);

            var result = await ReplayAsync(history, ReplayWorkflowVersion.Original);

            result.EnsureSucceeded();
            AssertOutcome(DurableTaskReplayOutcome.Waiting, result, root);

            // Storage which records no format holds the journal in the format which the source silo writes.
            var unlabeled = await DurableTaskHistory.CaptureAsync(
                new FormatlessJournalStorageProvider(capture.Storage),
                history.GrainId,
                DurableTaskRecoveryFixture.OrleansBinary,
                TestCancellation);
            Assert.Equal(DurableTaskRecoveryFixture.OrleansBinary, unlabeled.JournalFormat);

            var unlabeledResult = await ReplayAsync(unlabeled, ReplayWorkflowVersion.Original);

            unlabeledResult.EnsureSucceeded();
            AssertOutcome(DurableTaskReplayOutcome.Waiting, unlabeledResult, root);
        }
        finally
        {
            await capture.DisposeAsync();
        }
    }

    [Fact]
    public async Task GrainWhichNamesItsJournalStorageReplaysItsHistory()
    {
        const string key = "named-storage";
        var grain = fixture.Capture.Client.GetGrain<INamedStorageReplayGrain>(key);
        var root = TaskId.CreateRoot(key);
        await grain.HoldAsync(key).WithId(key).ScheduleAsync(TestCancellation);
        await fixture.CaptureProbe.WaitForArrivalAsync(key).WaitAsync(TimeSpan.FromSeconds(30), TestCancellation);
        var history = await DurableTaskHistory.CaptureAsync(fixture.Capture.Storage, grain.GetGrainId(), cancellationToken: TestCancellation);

        // The replay silo registers an empty provider under the grain's storage name, which the replay storage replaces.
        var result = await fixture.Replayer.ReplayAsync(history, TestCancellation);

        result.EnsureSucceeded();
        AssertOutcome(DurableTaskReplayOutcome.Waiting, result, root);
        Assert.Equal([root.Child("reserve")], result[root].Reached);
        Assert.Equal([root.Child("$signal:approved")], result[root].WaitingOn);
    }

    [Fact]
    public async Task UndecodableStoredRequestStallsTheReplay()
    {
        const string key = "undecodable-request";
        var grain = fixture.Capture.Client.GetGrain<IReplayOrderGrain>(key);
        var root = TaskId.CreateRoot(key);
        await grain.HoldAsync(new ReplayOrder { Id = key }).WithId(key).ScheduleAsync(TestCancellation);
        await fixture.CaptureProbe.WaitForArrivalAsync(key).WaitAsync(TimeSpan.FromSeconds(30), TestCancellation);
        var history = await DurableTaskHistory.CaptureAsync(fixture.Capture.Storage, grain.GetGrainId(), cancellationToken: TestCancellation);

        var result = await ReplayAsync(history, ReplayWorkflowVersion.UndecodableOrder);

        // Recovery quarantines the execution without starting an attempt, and the replay reports it.
        Assert.False(result.Succeeded);
        var execution = Assert.Single(result.Executions);
        Assert.Equal(root, execution.Id);
        AssertOutcome(DurableTaskReplayOutcome.Stalled, result, root);
        Assert.Equal(DurableTaskAttemptCancellationReason.Diverged, execution.Stall!.Reason);
        var divergence = Assert.IsType<DurableTaskDivergenceException>(execution.Stall.InnerException);
        Assert.Contains(ReplayOrderActivator.Error, divergence.Message, StringComparison.Ordinal);
    }

    private Task<(DurableTaskHistory History, TaskId Root)> CaptureAsync(string key, ReplayWorkflowVersion version) =>
        CaptureAsync(fixture.Capture, fixture.CaptureCode, fixture.CaptureProbe, key, version);

    /// <summary>
    /// Runs <paramref name="version"/> of the workflow until it waits at its last operation, and captures its grain's
    /// committed history.
    /// </summary>
    /// <remarks>
    /// Each version's operations before its last wait commit before the workflow arrives there, except the delay of
    /// <see cref="ReplayWorkflowVersion.Paused"/>, which commits with its own execution's first write.
    /// </remarks>
    private static async Task<(DurableTaskHistory History, TaskId Root)> CaptureAsync(
        DurableTaskRecoveryFixture capture,
        ReplayWorkflowCode code,
        ReplayWorkflowProbe probe,
        string key,
        ReplayWorkflowVersion version)
    {
        code.Version = version;
        using var events = DurableTaskEventBarriers.Collect();
        var grain = capture.Client.GetGrain<IReplayOrderGrain>(key);
        var root = TaskId.CreateRoot(key);
        await grain.FulfillAsync(key).WithId(key).ScheduleAsync(TestCancellation);
        await probe.WaitForArrivalAsync(key).WaitAsync(TimeSpan.FromSeconds(30), TestCancellation);
        if (version == ReplayWorkflowVersion.Paused)
        {
            // A local child's state commits with its first durable operation: the delay's attempt ends once its due time
            // and resume have committed.
            await events.WaitForExecutionStoppedAsync(grain.GetGrainId(), root.Child("pause"));
        }

        var history = await DurableTaskHistory.CaptureAsync(capture.Storage, grain.GetGrainId(), cancellationToken: TestCancellation);
        return (history, root);
    }

    /// <summary>Asserts the outcome of <paramref name="root"/>'s replay, and reports the replay when it differs.</summary>
    private static void AssertOutcome(DurableTaskReplayOutcome expected, DurableTaskReplayResult result, TaskId root) =>
        Assert.True(result[root].Outcome == expected, $"Expected {expected}.{Environment.NewLine}{result}");

    private Task<DurableTaskReplayResult> ReplayAsync(DurableTaskHistory history, ReplayWorkflowVersion version)
    {
        fixture.ReplayCode.Version = version;
        return fixture.Replayer.ReplayAsync(history, TestCancellation);
    }
}

/// <summary>A cluster which records workflow histories, and a replayer which replays them against other versions.</summary>
public sealed class DurableTaskReplayFixture : IAsyncLifetime
{
    public DurableTaskReplayFixture()
    {
        Capture = new(DurableTaskRecoveryFixture.Json, silo =>
        {
            silo.Services.AddSingleton(CaptureCode).AddSingleton(CaptureProbe);
            silo.Services.AddKeyedSingleton<IJournalStorageProvider>(
                NamedStorageReplayGrain.StorageName,
                static (services, _) => services.GetRequiredService<VolatileJournalStorageProvider>());
        });
    }

    internal DurableTaskRecoveryFixture Capture { get; }

    internal ReplayWorkflowCode CaptureCode { get; } = new();

    internal ReplayWorkflowProbe CaptureProbe { get; } = new();

    internal ReplayWorkflowCode ReplayCode { get; } = new();

    internal ReplayWorkflowProbe ReplayProbe { get; } = new();

    internal DurableTaskReplayer Replayer { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Capture.InitializeAsync();
        Replayer = await DurableTaskReplayer.StartAsync(
            silo =>
            {
                silo.Services.AddSingleton(ReplayCode).AddSingleton(ReplayProbe);
                silo.AddJournalStorage(NamedStorageReplayGrain.StorageName, static _ => new VolatileJournalStorageProvider());
            },
            TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Replayer.DisposeAsync();
        await Capture.DisposeAsync();
    }
}

/// <summary>The deployed versions of <see cref="ReplayOrderGrain"/>'s workflow.</summary>
public enum ReplayWorkflowVersion
{
    /// <summary>Reserves the order, ships it through the warehouse, and waits for approval.</summary>
    Original,

    /// <summary>Books a carrier instead of the warehouse, behind a version check.</summary>
    CarrierBooking,

    /// <summary>Books a carrier under the recorded shipment's ID, without a version check.</summary>
    UnguardedBooking,

    /// <summary>Retires the warehouse by raising the check's minimum supported version.</summary>
    RetiredWarehouse,

    /// <summary>Records an audit effect after shipping.</summary>
    Audited,

    /// <summary>Reserves the order and waits for a delay.</summary>
    Paused,

    /// <summary>Ships the order without waiting for approval.</summary>
    Unapproved,

    /// <summary>Changes <see cref="ReplayOrder"/> so that its recorded form no longer decodes.</summary>
    UndecodableOrder,
}

/// <summary>The version of the workflow which a silo deploys.</summary>
public sealed class ReplayWorkflowCode
{
    public ReplayWorkflowVersion Version { get; set; }
}

/// <summary>Reports when a workflow reaches its last wait, and counts the audit effects which run.</summary>
public sealed class ReplayWorkflowProbe
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _arrivals = new(StringComparer.Ordinal);
    private int _audits;

    public int Audits => Volatile.Read(ref _audits);

    public void Arrive(string key) => GetArrival(key).TrySetResult();

    public Task WaitForArrivalAsync(string key) => GetArrival(key).Task;

    public int Audit() => Interlocked.Increment(ref _audits);

    private TaskCompletionSource GetArrival(string key) =>
        _arrivals.GetOrAdd(key, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
}

public interface IReplayOrderGrain : IGrainWithStringKey
{
    DurableTask<string> FulfillAsync(string orderId);

    DurableTask<string> HoldAsync(ReplayOrder order);
}

public interface IReplayShippingGrain : IGrainWithStringKey
{
    DurableTask<string> ShipAsync(string orderId);

    DurableTask<string> BookAsync(string orderId);
}

public sealed class ReplayOrderGrain(ReplayWorkflowCode code, ReplayWorkflowProbe probe) : DurableGrain, IReplayOrderGrain
{
    public static readonly DurableSignal<string> Approved = new("approved");

    private IReplayShippingGrain Shipping => GrainFactory.GetGrain<IReplayShippingGrain>("main");

    public async DurableTask<string> HoldAsync(ReplayOrder order)
    {
        probe.Arrive(order.Id);
        return await Approved.WaitAsync();
    }

    public async DurableTask<string> FulfillAsync(string orderId)
    {
        await DurableTask.Run(static _ => { }).WithId("reserve");
        string shipment;
        switch (code.Version)
        {
            case ReplayWorkflowVersion.Original:
                shipment = await Shipping.ShipAsync(orderId).WithId("ship");
                break;
            case ReplayWorkflowVersion.CarrierBooking:
                shipment = DurableTask.GetVersion("carrier-booking", minSupported: 0, maxSupported: 1) >= 1
                    ? await Shipping.BookAsync(orderId).WithId("book")
                    : await Shipping.ShipAsync(orderId).WithId("ship");
                break;
            case ReplayWorkflowVersion.UnguardedBooking:
                shipment = await Shipping.BookAsync(orderId).WithId("ship");
                break;
            case ReplayWorkflowVersion.RetiredWarehouse:
                DurableTask.GetVersion("carrier-booking", minSupported: 1, maxSupported: 1);
                shipment = await Shipping.BookAsync(orderId).WithId("book");
                break;
            case ReplayWorkflowVersion.Audited:
                shipment = await Shipping.ShipAsync(orderId).WithId("ship");
                DurableTask.RunInline("audit", probe.Audit);
                break;
            case ReplayWorkflowVersion.Unapproved:
                return await Shipping.ShipAsync(orderId).WithId("ship");
            case ReplayWorkflowVersion.Paused:
                var pause = await DurableTask.Delay(TimeSpan.FromMinutes(30)).WithId("pause").ScheduleAsync();
                probe.Arrive(orderId);
                await pause;
                return "resumed";
            default:
                throw new InvalidOperationException($"Unknown workflow version {code.Version}.");
        }

        probe.Arrive(orderId);
        return $"{shipment}: {await Approved.WaitAsync()}";
    }
}

public sealed class ReplayShippingGrain : DurableGrain, IReplayShippingGrain
{
    public DurableTask<string> ShipAsync(string orderId) => DurableTask.FromResult($"shipped {orderId}");

    public DurableTask<string> BookAsync(string orderId) => DurableTask.FromResult($"booked {orderId}");
}

/// <summary>An order whose recorded form a silo which deploys <see cref="ReplayWorkflowVersion.UndecodableOrder"/> can't decode.</summary>
[GenerateSerializer, Immutable, UseActivator, Alias("durable-task-replay-order-test")]
public sealed class ReplayOrder
{
    [Id(0)]
    public string Id { get; init; } = "";
}

[RegisterActivator]
public sealed class ReplayOrderActivator(IServiceProvider services) : IActivator<ReplayOrder>
{
    public const string Error = "The order's recorded form no longer decodes.";

    public ReplayOrder Create() =>
        services.GetService<ReplayWorkflowCode>()?.Version == ReplayWorkflowVersion.UndecodableOrder
            ? throw new InvalidOperationException(Error)
            : new();
}

public interface INamedStorageReplayGrain : IGrainWithStringKey
{
    DurableTask<string> HoldAsync(string orderId);
}

/// <summary>A workflow whose grain type journals to the provider which it names.</summary>
[JournalStorageProvider(StorageName)]
public sealed class NamedStorageReplayGrain(ReplayWorkflowProbe probe) : DurableGrain, INamedStorageReplayGrain
{
    public const string StorageName = "replay-orders";

    public async DurableTask<string> HoldAsync(string orderId)
    {
        await DurableTask.Run(static _ => { }).WithId("reserve");
        probe.Arrive(orderId);
        return await ReplayOrderGrain.Approved.WaitAsync();
    }
}

/// <summary>Reads another provider's journals without their format, as storage which records none does.</summary>
internal sealed class FormatlessJournalStorageProvider(IJournalStorageProvider inner) : IJournalStorageProvider
{
    public IJournalStorage CreateStorage(JournalId journalId) => new Storage(inner.CreateStorage(journalId));

    private sealed class Storage(IJournalStorage inner) : IJournalStorage
    {
        public bool IsCompactionRequested => inner.IsCompactionRequested;

        public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken) =>
            inner.ReadAsync(new Consumer(consumer), cancellationToken);

        public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => inner.ReplaceAsync(value, cancellationToken);

        public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken) => inner.AppendAsync(value, cancellationToken);

        public ValueTask DeleteAsync(CancellationToken cancellationToken) => inner.DeleteAsync(cancellationToken);
    }

    private sealed class Consumer(IJournalStorageConsumer inner) : IJournalStorageConsumer
    {
        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata) => inner.Read(buffer, metadata: null);
    }
}
