using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Configuration;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Runtime.Diagnostics;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxMissingHandlerTests() : DurableMessagingBehaviorTestBase(new RetryFixture())
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AcceptedHandlerMissingOnFreshActivation_DeadLettersImmediatelyAndRetainsDedupe()
    {
        var receiver = NewGrain();
        const string route = "activation-only/handler";
        Assert.True((await receiver.RegisterDuplicateExactRouteHandlersAsync(route)).LookupRetainedFirstHandler);
        var originalContext = Fixture.GetGrainContext(receiver);
        var originalGrain = Assert.IsType<DurableMessagingTestGrain>(originalContext.GrainInstance);
        using var envelope = CreateEnvelope(receiver, NewMessage(190, "missing-after-replay"), route);
        Assert.Equal(DeliveryStatus.Accepted, (await receiver.AcceptAndDeactivateAsync(envelope.Value)).Status);
        await originalContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        var accepted = originalGrain.GetSnapshotForTest();
        Assert.Equal(1, accepted.InboxCount);
        Assert.Empty(accepted.Effects);
        Assert.Equal(0, accepted.FirstExactRouteHandlerCalls);
        var job = Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
        var now = Fixture.Clock.GetUtcNow();
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var stopped = WaitForPumpAsync(events, receiver.GetGrainId());

        _ = await receiver.GetSnapshotAsync();
        await stopped;
        var completed = await receiver.GetSnapshotAsync();

        Assert.NotEqual(accepted.ActivationId, completed.ActivationId);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Empty(completed.Effects);
        Assert.Equal(0, completed.OutboxCount);
        Assert.Equal(0, completed.MaxConcurrentHandlers);
        Assert.Equal(0, completed.FirstExactRouteHandlerCalls);
        Assert.Equal(0, completed.GenericExactRouteHandlerCalls);
        var deadLetter = Assert.Single(completed.InboxDeadLetters);
        Assert.Equal(envelope.Value.MessageId, deadLetter.MessageId);
        Assert.Equal("No compatible handler is registered.", deadLetter.Reason);
        Assert.Equal(0, deadLetter.AttemptCount);
        Assert.Equal(now, deadLetter.DeadLetteredAt);
        var context = Fixture.GetGrainContext(receiver);
        var retained = Assert.Single(context.ActivationServices.GetRequiredService<IDurableMessagingDiagnostics>().InboxDeadLetters);
        Assert.Equal(envelope.Value.SenderId, retained.Message.SenderId);
        Assert.Empty(GetAttemptStates(context));
        var processed = context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DateTimeOffset>>("__orleans.durable-messaging.inbox-processed");
        Assert.Equal(now, processed[(envelope.Value.SenderId, envelope.Value.MessageId)]);
        Assert.Equal(job.Id, completed.InboxJob!.Id);
        Assert.Equal(job.ShardId, completed.InboxJob.ShardId);
        Assert.Equal(accepted.InboxJobId, completed.InboxJobId);
        Assert.Equal(1, ScheduleCount(receiver));
        Assert.False(Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance).Faulted.Task.IsCompleted);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);

        Assert.Equal(DurableJobRunStatus.Completed, (await RunPumpAsync(receiver, job)).Status);
        var retired = await receiver.GetSnapshotAsync();
        Assert.Null(retired.InboxJobId);
        Assert.Null(retired.InboxJob);
        Assert.Equal(1, ScheduleCount(receiver));
        await receiver.RequestDeactivationAsync();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Cancellation);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(completed.ActivationId, recovered.ActivationId);
        Assert.Equal(deadLetter, Assert.Single(recovered.InboxDeadLetters));
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Empty(recovered.Effects);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
        Fixture.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
        Fixture.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(DeliveryStatus.RouteNotFound, (await DeliverAsync(receiver, envelope.Value)).Status);
        Assert.Equal(deadLetter, Assert.Single((await receiver.GetSnapshotAsync()).InboxDeadLetters));
        Assert.Equal(1, ScheduleCount(receiver));
    }

    [Fact]
    public async Task OrdinaryHandlerFailures_RetryUntilExactConfiguredAttemptLimit()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(191, "retry-limit") with { ThrowDuringPreparation = true });
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        var stopped = WaitForPumpAsync(events, receiver.GetGrainId());
        var now = Fixture.Clock.GetUtcNow();
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await stopped;
        var first = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        AssertPendingRetry(context, first, expectedAttempts: 1, now + TimeSpan.FromMinutes(1));
        var job = Assert.IsType<DurableJob>(first.InboxJob);
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, (await RunPumpAsync(receiver, job)).Status);
        AssertPendingRetry(context, await receiver.GetSnapshotAsync(), expectedAttempts: 1, now + TimeSpan.FromMinutes(1));
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));

        Fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(DurableJobRunStatus.RescheduleRequested, (await RunPumpAsync(receiver, job)).Status);
        AssertPendingRetry(context, await receiver.GetSnapshotAsync(), expectedAttempts: 2, now + TimeSpan.FromMinutes(3));
        Fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(DurableJobRunStatus.Completed, (await RunPumpAsync(receiver, job)).Status);
        var completed = await receiver.GetSnapshotAsync();
        var deadLetter = Assert.Single(completed.InboxDeadLetters);
        Assert.Equal(3, deadLetter.AttemptCount);
        Assert.Contains("Injected handler preparation failure", deadLetter.Reason, StringComparison.Ordinal);
        Assert.Equal(envelope.Value.MessageId, deadLetter.MessageId);
        Assert.Empty(completed.Effects);
        Assert.Equal(0, completed.OutboxCount);
        Assert.Equal(0, completed.InboxCount);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Empty(GetAttemptStates(context));
        Assert.Null(completed.InboxJobId);
        Assert.Equal(1, ScheduleCount(receiver));
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
        Assert.False(Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance).Faulted.Task.IsCompleted);
    }

    private static void AssertPendingRetry(IGrainContext context, DurableEndpointSnapshot snapshot, int expectedAttempts, DateTimeOffset nextAttempt)
    {
        Assert.Equal(1, snapshot.InboxCount);
        Assert.Equal(0, snapshot.ProcessedMessageCount);
        Assert.Empty(snapshot.InboxDeadLetters);
        Assert.Empty(snapshot.Effects);
        Assert.Equal(0, snapshot.OutboxCount);
        var entry = Assert.Single(GetAttemptStates(context).Cast<object>());
        var state = entry.GetType().GetProperty("Value")!.GetValue(entry)!;
        Assert.Equal(expectedAttempts, state.GetType().GetProperty("AttemptCount")!.GetValue(state));
        Assert.Equal(nextAttempt, state.GetType().GetProperty("NextAttemptAt")!.GetValue(state));
    }
    private static IEnumerable GetAttemptStates(IGrainContext context)
    {
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        Assert.True(manager.TryGetState("__orleans.durable-messaging.inbox-message-state", out var state));
        return Assert.IsAssignableFrom<IEnumerable>(state);
    }
    private int ScheduleCount(IDurableMessagingTestGrain receiver) =>
        Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, receiver.GetGrainId());
    private static Task<DiagnosticEvent> WaitForPumpAsync(DiagnosticEventCollector events, GrainId grainId) =>
        events.WaitForEventAsync(nameof(GrainTimerEvents.TickStop),
            item => item.Payload is GrainTimerEvents.TickStop stop && stop.GrainContext.GrainId == grainId,
            TimeSpan.FromSeconds(30), Cancellation);
    private async Task<DurableJobRunResult> RunPumpAsync(IDurableMessagingTestGrain receiver, DurableJob job)
    {
        var context = Fixture.GetGrainContext(receiver);
        var feature = (IDurableJobFeatureHandler)context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        var run = new PumpContext(job);
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        DurableJobRunResult result = null!;
        await OnTurnAsync(context, () => result = feature.ExecuteJobAsync(run, Cancellation).GetAwaiter().GetResult());
        if (result.IsInProgress)
        {
            var stopped = await WaitForPumpAsync(events, receiver.GetGrainId());
            Assert.Null(Assert.IsType<GrainTimerEvents.TickStop>(stopped.Payload).Exception);
            await OnTurnAsync(context, () => result = feature.ExecuteJobAsync(run, Cancellation).GetAwaiter().GetResult());
        }
        return result;
    }
    private static Task OnTurnAsync(IGrainContext context, Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception exception) { done.SetException(exception); }
        });
        return done.Task;
    }
    private sealed class PumpContext(DurableJob job) : IJobRunContext
    {
        public DurableJob Job { get; } = job;
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public int DequeueCount => 1;
    }
    private sealed class RetryFixture : DurableMessagingClusterFixture
    {
        protected override void ConfigureOptions(DurableInboxOptions options)
        {
            base.ConfigureOptions(options);
            options.MaxProcessingAttempts = 3;
            options.BackpressureRetryDelay = TimeSpan.FromMinutes(1);
        }
    }
}
