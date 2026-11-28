using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Documentation.Grains.DurableTasks.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Durable.Journaling;
using Orleans.Durable.Messaging;
using Orleans.Durable.Messaging.Configuration;
using Orleans.Durable.Messaging.Diagnostics;
using Orleans.Durable.Tasks;
using Orleans.Durable.Tasks.Runtime;
using Orleans.Durable.Tasks.Testing;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
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
        var history = await DurableTaskHistory.CaptureAsync(fixture.Capture.Storage, grain.GetGrainId(), cancellationToken: TestCancellation);
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

    [Fact]
    public async Task DueInboxMessagesStayPendingInTheReplay()
    {
        // One history ends while a handler attempt runs; the other ends with the clean stop of its activation.
        var interrupted = await CaptureHeldInboxMessageAsync("inbox-interrupted", stopCleanly: false);
        var stopped = await CaptureHeldInboxMessageAsync("inbox-stopped", stopCleanly: true);
        using var processed = new InboxProcessedCounter(ReplayInboxGrain.GrainTypeName);

        var interruptedResult = await fixture.Replayer.ReplayAsync(interrupted, TestCancellation);
        var stoppedResult = await fixture.Replayer.ReplayAsync(stopped, TestCancellation);

        // Recovery charges no interrupted attempt, and the pump dispatches neither message.
        interruptedResult.EnsureSucceeded();
        stoppedResult.EnsureSucceeded();
        Assert.Equal(0, fixture.ReplayInboxProbe.Handled);
        Assert.Equal(0, processed.Count);
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

        var history = await DurableTaskHistory.CaptureAsync(capture.Storage, grain.GetGrainId(), cancellationToken: TestCancellation);
        return (history, root);
    }

    [Fact]
    public async Task UndeliveredOutboxEntryStaysAtItsRecordedState()
    {
        const string key = "outbox-pending";
        fixture.CaptureCode.Version = ReplayWorkflowVersion.Unapproved;
        var grain = fixture.Capture.Client.GetGrain<IReplayOrderGrain>(key);
        var root = TaskId.CreateRoot(key);
        var hold = fixture.CaptureProbe.HoldDeliveriesFrom(grain.GetGrainId());
        try
        {
            // The outbox committed the call, whose first delivery waits at its receiver, so the history holds the entry
            // before any attempt: due at once, with no retry time.
            await grain.FulfillAsync(key).WithId(key).ScheduleAsync(TestCancellation);
            await hold.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(30), TestCancellation);
            var history = await DurableTaskHistory.CaptureAsync(fixture.Capture.Storage, grain.GetGrainId(), cancellationToken: TestCancellation);
            using var events = new DiagnosticEventCollector(MessagingEvents.ListenerName);

            var result = await ReplayAsync(history, ReplayWorkflowVersion.Unapproved);

            // The replay silo's outbox makes no attempt, so it stages no retry or dead letter for the entry.
            result.EnsureSucceeded();
            AssertOutcome(DurableTaskReplayOutcome.Waiting, result, root);
            Assert.Equal([root.Child("ship")], result[root].WaitingOn);
            Assert.DoesNotContain(events.Events, evt => evt.Payload is MessagingEvents.OutboxRetryScheduled or MessagingEvents.OutboxMessageRetired
                && ((MessagingEvents.MessagingEvent)evt.Payload).GrainContext.GrainId == grain.GetGrainId());
        }
        finally
        {
            hold.Release();
        }
    }

    [Fact]
    public async Task OrdinaryGrainCallFromReplayedCodeDoesNotReachItsTarget()
    {
        var (history, root) = await CaptureAsync("ordinary-call", ReplayWorkflowVersion.Original);
        var pings = fixture.ReplayProbe.Pings;

        var result = await ReplayAsync(history, ReplayWorkflowVersion.Pinged);

        // The replay silo rejects the call without activating its target, and the body fails with the rejection.
        Assert.Equal(pings, fixture.ReplayProbe.Pings);
        AssertOutcome(DurableTaskReplayOutcome.Faulted, result, root);
        Assert.Contains("was rejected", result[root].Response!.Exception!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedCleanupDoesNotReplaceTheReplaysOutcome()
    {
        const string key = "blocked";
        var (history, root) = await CaptureAsync(key, ReplayWorkflowVersion.Original);
        var code = new ReplayWorkflowCode { Version = ReplayWorkflowVersion.Blocked };
        var probe = new ReplayWorkflowProbe();
        await using var replayer = await DurableTaskReplayer.StartAsync(
            silo => silo.Services.AddSingleton(code).AddSingleton(probe).AddSingleton(new ReplayInboxProbe()),
            TestCancellation);
        replayer.DeactivationTimeout = TimeSpan.FromSeconds(1);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var replay = replayer.ReplayAsync(history, cancellation.Token);
        await probe.WaitForArrivalAsync(key).WaitAsync(TimeSpan.FromSeconds(30), TestCancellation);

        // The body awaits work which ignores its attempt's cancellation, so the grain doesn't deactivate; the replay reports
        // the caller's cancellation all the same, and later replays wait for the activation to stop.
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay);
        var overlap = await Assert.ThrowsAsync<InvalidOperationException>(() => replayer.ReplayAsync(history, TestCancellation));
        Assert.Contains("hasn't stopped", overlap.Message, StringComparison.Ordinal);

        // Once the body's work completes, the activation stops, and the replayer replays the history again.
        probe.ReleaseBlocked();
        code.Version = ReplayWorkflowVersion.Original;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        DurableTaskReplayResult result;
        while (true)
        {
            try
            {
                result = await replayer.ReplayAsync(history, TestCancellation);
                break;
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("hasn't stopped", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), TestCancellation);
            }
        }

        result.EnsureSucceeded();
        AssertOutcome(DurableTaskReplayOutcome.Waiting, result, root);
    }

    /// <summary>
    /// Captures the history of a grain whose inbox holds a message with neither a delivery time nor a retry time, whose
    /// handler attempt the capture interrupts or the grain's clean stop ends.
    /// </summary>
    private async Task<DurableTaskHistory> CaptureHeldInboxMessageAsync(string key, bool stopCleanly)
    {
        var grain = fixture.Capture.Client.GetGrain<IReplayInboxGrain>(key);
        var delivery = await grain.AsReference<IMessagingExtension>().DeliverAsync(
            new InboxMessage
            {
                Envelope = new() { MessageId = MessageId.Create(key), Subject = ReplayInboxGrain.Subject, Payload = [] },
                SentAt = fixture.CaptureTime.GetUtcNow(),
            },
            TestCancellation);
        Assert.IsType<DeliveryResult.Accepted>(delivery.Value);
        await fixture.CaptureInboxProbe.WaitForAttemptAsync(key).WaitAsync(TimeSpan.FromSeconds(30), TestCancellation);
        if (stopCleanly)
        {
            // Deactivation cancels the attempt and journals the clean stop; the message's wake-up can't reactivate the
            // grain before the capture.
            fixture.CaptureInboxProbe.BlockActivation = true;
            await grain.RequestDeactivationAsync();
            var management = fixture.Capture.Client.GetGrain<IManagementGrain>(0);
            while (await management.GetActivationAddress(grain) is not null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), TestCancellation);
            }
        }

        return await DurableTaskHistory.CaptureAsync(fixture.Capture.Storage, grain.GetGrainId(), cancellationToken: TestCancellation);
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
            silo.Services.AddSingleton(CaptureInboxProbe);
            silo.Services.AddSingleton<IIncomingGrainCallFilter, HeldDeliveryFilter>();
            silo.Services.Configure<DurableMessagingOptions>(options => options.RetryInitialInterval = TimeSpan.FromHours(1));
            silo.Services.AddKeyedSingleton<IJournalStorageProvider>(
                NamedStorageReplayGrain.StorageName,
                static (services, _) => services.GetRequiredService<VolatileJournalStorageProvider>());
        });
        Capture.JobClock.Advance(-TimeSpan.FromDays(30));
    }

    internal DurableTaskRecoveryFixture Capture { get; }

    internal ReplayWorkflowCode CaptureCode { get; } = new();

    internal ReplayWorkflowProbe CaptureProbe { get; } = new();

    internal ReplayInboxProbe CaptureInboxProbe { get; } = new() { HoldsAttempts = true };

    internal ReplayInboxProbe ReplayInboxProbe { get; } = new();

    internal AdvanceableTimeProvider CaptureTime { get; } = new();

    internal ReplayWorkflowCode ReplayCode { get; } = new();

    internal ReplayWorkflowProbe ReplayProbe { get; } = new();

    internal DurableTaskReplayer Replayer { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Capture.InitializeAsync();
        Replayer = await DurableTaskReplayer.StartAsync(
            silo =>
            {
                silo.Services.AddSingleton(ReplayCode).AddSingleton(ReplayProbe).AddSingleton(ReplayInboxProbe);
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

    /// <summary>Ships the order, then awaits work which ignores cancellation and completes only when the test releases it.</summary>
    Blocked,

    /// <summary>Ships the order, then makes an ordinary grain call to the shipping grain.</summary>
    Pinged,
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
    private readonly ConcurrentDictionary<GrainId, DeliveryHold> _deliveryHolds = new();
    private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _audits;
    private int _pings;

    public int Audits => Volatile.Read(ref _audits);

    public int Pings => Volatile.Read(ref _pings);

    public void Ping() => Interlocked.Increment(ref _pings);

    /// <summary>Returns work which ignores cancellation and completes once <see cref="ReleaseBlocked"/> is called.</summary>
    public Task BlockAsync() => _blocked.Task;

    public void ReleaseBlocked() => _blocked.TrySetResult();

    /// <summary>Holds the deliveries which <paramref name="sender"/>'s outbox makes, at their receivers, until released.</summary>
    public DeliveryHold HoldDeliveriesFrom(GrainId sender) => _deliveryHolds.GetOrAdd(sender, static _ => new());

    public bool TryGetDeliveryHold(GrainId sender, [NotNullWhen(true)] out DeliveryHold? hold) => _deliveryHolds.TryGetValue(sender, out hold);

    public void Arrive(string key) => GetArrival(key).TrySetResult();

    public Task WaitForArrivalAsync(string key) => GetArrival(key).Task;

    public int Audit() => Interlocked.Increment(ref _audits);

    private TaskCompletionSource GetArrival(string key) =>
        _arrivals.GetOrAdd(key, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    public sealed class DeliveryHold
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once a held delivery reaches its receiver.</summary>
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Released => _released.Task;

        public void Release() => _released.TrySetResult();
    }
}

/// <summary>Holds the deliveries which <see cref="ReplayWorkflowProbe.HoldDeliveriesFrom"/> selects at their receivers.</summary>
public sealed class HeldDeliveryFilter(ReplayWorkflowProbe probe) : IIncomingGrainCallFilter
{
    public async Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.MethodName == nameof(IMessagingExtension.DeliverAsync)
            && context.SourceId is { } sender
            && probe.TryGetDeliveryHold(sender, out var hold))
        {
            hold.Arrived.TrySetResult();
            await hold.Released;
        }

        await context.Invoke();
    }
}

public interface IReplayOrderGrain : IGrainWithStringKey
{
    DurableTask<string> FulfillAsync(string orderId);

    DurableTask<string> HoldAsync(ReplayOrder order);

    Task FlushAsync();
}

public interface IReplayShippingGrain : IGrainWithStringKey
{
    DurableTask<string> ShipAsync(string orderId);

    DurableTask<string> BookAsync(string orderId);

    Task PingAsync();
}

public sealed class ReplayOrderGrain(ReplayWorkflowCode code, ReplayWorkflowProbe probe) : DurableGrain, IReplayOrderGrain
{
    public static readonly DurableSignal<string> Approved = new("approved");

    private IReplayShippingGrain Shipping => GrainFactory.GetGrain<IReplayShippingGrain>("main");

    public Task FlushAsync() => StateManager.WriteStateAsync().AsTask();

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
            case ReplayWorkflowVersion.Blocked:
                shipment = await Shipping.ShipAsync(orderId).WithId("ship");
                probe.Arrive(orderId);
                await probe.BlockAsync();
                break;
            case ReplayWorkflowVersion.Pinged:
                shipment = await Shipping.ShipAsync(orderId).WithId("ship");
                await Shipping.PingAsync();
                break;
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

public sealed class ReplayShippingGrain(ReplayWorkflowProbe probe) : DurableGrain, IReplayShippingGrain
{
    public Task PingAsync()
    {
        probe.Ping();
        return Task.CompletedTask;
    }

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

public interface IReplayInboxGrain : IGrainWithStringKey
{
    Task RequestDeactivationAsync();
}

/// <summary>A durable grain which also handles inbox messages of its own.</summary>
[GrainType(GrainTypeName)]
public sealed class ReplayInboxGrain : DurableGrain, IReplayInboxGrain, IInboxHandler
{
    public const string GrainTypeName = "durable-task-replay-inbox-test";
    public const string Subject = "replay-test.note";
    private readonly ReplayInboxProbe _probe;

    public ReplayInboxGrain(IInbox inbox, ReplayInboxProbe probe)
    {
        if (probe.BlockActivation)
        {
            throw new InvalidOperationException("The grain is unavailable while its history is captured.");
        }

        _probe = probe;
        inbox.Register(this);
    }

    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public bool CanHandle(InboxMessage message) => message.Envelope.Subject == Subject;

    public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        await _probe.AttemptAsync(this.GetPrimaryKeyString(), cancellationToken);
        context.Complete();
    }
}

/// <summary>Counts <see cref="ReplayInboxGrain"/>'s handler attempts, and holds them until they are canceled.</summary>
public sealed class ReplayInboxProbe
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _attempts = new(StringComparer.Ordinal);
    private int _handled;

    public bool HoldsAttempts { get; init; }

    public volatile bool BlockActivation;

    public int Handled => Volatile.Read(ref _handled);

    public Task WaitForAttemptAsync(string key) => GetAttempt(key).Task;

    public async Task AttemptAsync(string key, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _handled);
        GetAttempt(key).TrySetResult();
        if (HoldsAttempts)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private TaskCompletionSource GetAttempt(string key) =>
        _attempts.GetOrAdd(key, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>Counts the inbox messages which grains of one type process, including attempts which recovery charges.</summary>
internal sealed class InboxProcessedCounter : IDisposable
{
    private readonly MeterListener _listener = new();
    private long _count;

    public InboxProcessedCounter(string grainType)
    {
        _listener.InstrumentPublished = static (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Microsoft.Orleans" && instrument.Name == "orleans-messaging-inbox-messages-processed")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "grain_type" && Equals(tag.Value, grainType))
                {
                    Interlocked.Add(ref _count, measurement);
                }
            }
        });
        _listener.Start();
    }

    public long Count => Interlocked.Read(ref _count);

    public void Dispose() => _listener.Dispose();
}
