using System.Collections.Concurrent;
using Documentation.Grains.DurableTasks.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Testing;
using Orleans.Journaling;
using Orleans.Messaging.Configuration;
using Orleans.Messaging.Diagnostics;
using Orleans.Runtime;
using Orleans.TestingHost.Diagnostics;
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
    public async Task PastDueRecordedDelayKeepsTheReplayWaiting()
    {
        var (history, root) = await CaptureAsync("delay", ReplayWorkflowVersion.Paused);

        var result = await ReplayAsync(history, ReplayWorkflowVersion.Paused);

        Assert.True(fixture.CaptureTime.GetUtcNow() + TimeSpan.FromMinutes(30) < history.CapturedAt);
        result.EnsureSucceeded();
        AssertOutcome(DurableTaskReplayOutcome.Waiting, result, root);
        Assert.Equal([root.Child("pause")], result[root].WaitingOn);
    }

    [Fact]
    public async Task ExpiredPendingInvocationKeepsTheReplayWaiting()
    {
        const string key = "expired-invocation";
        fixture.CaptureCode.Version = ReplayWorkflowVersion.Unapproved;
        var grain = fixture.Capture.Client.GetGrain<IReplayOrderGrain>(key);
        var shipping = fixture.Capture.Client.GetGrain<IReplayShippingGrain>("main").GetGrainId();
        var root = TaskId.CreateRoot(key);
        using var failures = new DeliveryFaultInjector(receiving => receiving.GrainContext.GrainId == shipping, "Shipping is unavailable.");
        using var events = new DiagnosticEventCollector(MessagingEvents.ListenerName);
        await grain.FulfillAsync(key).WithId(key).ScheduleAsync(TestCancellation);
        var retry = (MessagingEvents.OutboxRetryScheduled)(await events.WaitForEventAsync(
            nameof(MessagingEvents.OutboxRetryScheduled),
            evt => evt.Payload is MessagingEvents.OutboxRetryScheduled retry && retry.GrainContext.GrainId == grain.GetGrainId(),
            TimeSpan.FromSeconds(30),
            TestCancellation)).Payload!;
        await grain.FlushAsync();
        var history = await DurableTaskHistory.CaptureAsync(fixture.Capture.Storage, grain.GetGrainId(), TestCancellation);
        Assert.True(retry.NextAttemptAt + TimeSpan.FromHours(6) < history.CapturedAt);

        var result = await ReplayAsync(history, ReplayWorkflowVersion.Unapproved);

        result.EnsureSucceeded();
        AssertOutcome(DurableTaskReplayOutcome.Waiting, result, root);
        Assert.Equal([root.Child("ship")], result[root].WaitingOn);
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
        }
        finally
        {
            await capture.DisposeAsync();
        }
    }

    [Fact]
    public async Task DocumentationCaptureReplaysVersionZeroAtTheApprovalWait()
    {
        var path = Path.GetTempFileName();
        var progress = new FulfillmentProgress();
        var cluster = new DurableTaskRecoveryFixture(
            DurableTaskRecoveryFixture.Json,
            silo => DurableTaskTestingSnippets.ConfigureCaptureSilo(silo, progress));
        try
        {
            await cluster.InitializeAsync();
            Assert.Equal(2, cluster.Cluster.Silos.Count);
            Assert.All(cluster.Cluster.Silos, silo => Assert.Same(progress, silo.ServiceProvider.GetRequiredService<FulfillmentProgress>()));
            const string orderId = "documentation";
            await DurableTaskTestingSnippets.CaptureAsync(cluster.Cluster, orderId, path);
            await using var replayer = await DurableTaskReplayer.StartAsync(
                silo => silo.Services.AddSingleton<FulfillmentCode>().AddSingleton<FulfillmentProgress>(),
                TestCancellation);
            var history = await DurableTaskHistory.LoadAsync(path, TestCancellation);

            var result = await replayer.ReplayAsync(history, TestCancellation);

            result.EnsureSucceeded();
            var root = TaskId.CreateRoot($"fulfill-{orderId}");
            AssertOutcome(DurableTaskReplayOutcome.Waiting, result, root);
            Assert.Equal([root.Child("ship")], result[root].Reached);
            Assert.Equal(0, result[root].Versions["carrier-booking"]);
            Assert.Equal([root.Child("$signal:approved")], result[root].WaitingOn);
            await DurableTaskTestingSnippets.ReplayVersionedChangeAsync(replayer, path, orderId);
        }
        finally
        {
            await cluster.DisposeAsync();
            File.Delete(path);
        }
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

        var history = await DurableTaskHistory.CaptureAsync(capture.Storage, grain.GetGrainId(), TestCancellation);
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
        CaptureTime.Advance(-TimeSpan.FromDays(30));
        Capture = new(DurableTaskRecoveryFixture.Json, silo =>
        {
            silo.Services.AddSingleton(CaptureCode).AddSingleton(CaptureProbe).AddSingleton<TimeProvider>(CaptureTime);
            silo.Services.Configure<DurableMessagingOptions>(options => options.RetryInitialInterval = TimeSpan.FromHours(1));
        });
        Capture.JobClock.Advance(-TimeSpan.FromDays(30));
    }

    internal DurableTaskRecoveryFixture Capture { get; }

    internal ReplayWorkflowCode CaptureCode { get; } = new();

    internal ReplayWorkflowProbe CaptureProbe { get; } = new();

    internal AdvanceableTimeProvider CaptureTime { get; } = new();

    internal ReplayWorkflowCode ReplayCode { get; } = new();

    internal ReplayWorkflowProbe ReplayProbe { get; } = new();

    internal DurableTaskReplayer Replayer { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Capture.InitializeAsync();
        Replayer = await DurableTaskReplayer.StartAsync(
            silo => silo.Services.AddSingleton(ReplayCode).AddSingleton(ReplayProbe),
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

    Task FlushAsync();
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

    public Task FlushAsync() => StateManager.WriteStateAsync().AsTask();

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
