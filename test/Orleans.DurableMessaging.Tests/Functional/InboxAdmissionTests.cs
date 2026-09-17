using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class InboxAdmissionTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task HandlerAndOutgoingPreparation_BlockQueuedWritesUntilAtomicCapture()
    {
        using var attempt = await PrepareAttemptAsync("atomic-admission");
        var state = attempt.Grain.GetSnapshotForTest();
        Assert.Equal(1, state.InboxCount);
        Assert.Equal(0, state.ProcessedMessageCount);
        Assert.Equal(1, Assert.Single(state.Effects).Count);
        Assert.Equal(1, attempt.Outbox.Count);
        var writes = Fixture.Storage.GetSuccessfulWriteCount(attempt.JournalId);
        var storage = Fixture.Storage.BlockWrite(attempt.JournalId);
        var queued = attempt.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        var coalesced = attempt.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(queued.IsCompleted);
        Assert.False(coalesced.IsCompleted);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(attempt.JournalId));

        attempt.Preparation.Release();
        await storage.WaitUntilEnteredAsync();
        var captured = attempt.Grain.GetSnapshotForTest();
        Assert.Equal(0, captured.InboxCount);
        Assert.Equal(1, captured.ProcessedMessageCount);
        Assert.Equal(1, Assert.Single(captured.Effects).Count);
        Assert.Single(attempt.Outbox.LastCapturedIds);
        Assert.False(queued.IsCompleted);
        storage.Release();
        await Task.WhenAll(queued, coalesced);
        await attempt.Receiver.RequestDeactivationAsync();
        var recovered = await attempt.Receiver.GetSnapshotAsync();
        Assert.NotEqual(state.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(1, recovered.ProcessedMessageCount);
        Assert.Equal(1, recovered.OutboxCount);
        Assert.Equal(0, recovered.InboxCount);
    }

    [Fact]
    public async Task OutgoingPreparationFault_FencesBothEndpointsBeforeQueuedWaitersResume()
    {
        using var attempt = await PrepareAttemptAsync("fault-ordering");
        var extension = attempt.Context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        var queued = attempt.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        var failure = new IOException("Injected outgoing prerequisite failure.");
        var writes = Fixture.Storage.GetSuccessfulWriteCount(attempt.JournalId);
        attempt.Preparation.Fail(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => queued));
        Assert.Same(failure, await attempt.Grain.Faulted.Task);
        Assert.Same(failure, attempt.Outbox.Failure);
        await attempt.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(async () =>
            await ((IDurableInboxExtension)extension).DeliverAsync(attempt.Envelope, TestContext.Current.CancellationToken)));
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(attempt.JournalId));
        var failed = attempt.Grain.GetSnapshotForTest();
        Assert.Single(failed.Effects);
        Assert.Equal(1, failed.InboxCount);
        Assert.Equal(0, failed.ProcessedMessageCount);
        _ = await attempt.Receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForEffectCountAsync(attempt.Receiver, 1);
        Assert.NotEqual(failed.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(1, recovered.ProcessedMessageCount);
    }

    [Fact]
    public async Task PhysicalOwnerInvalidationAfterHandler_FailsWholeOperationBeforeCapture()
    {
        using var attempt = await PrepareAttemptAsync("owner-invalidation");
        var value = attempt.Context.ActivationServices.GetRequiredKeyedService<IDurableValue<DurableJob>>("__orleans.durable-messaging.inbox-job-handle");
        var previous = value.Value!;
        attempt.Outbox.BeforeFinalization = () => value.Value = new DurableJob
        {
            Id = previous.Id,
            ShardId = "different-physical-shard",
            Metadata = previous.Metadata,
            Name = previous.Name,
            TargetGrainId = previous.TargetGrainId,
            DueTime = previous.DueTime
        };
        var writes = Fixture.Storage.GetSuccessfulWriteCount(attempt.JournalId);
        var queued = attempt.Manager.WriteStateAsync(TestContext.Current.CancellationToken).AsTask();
        attempt.Preparation.Release();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        Assert.Contains("acknowledged physical job", failure.Message, StringComparison.Ordinal);
        Assert.Same(failure, attempt.Outbox.Failure);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(attempt.JournalId));
        var failed = attempt.Grain.GetSnapshotForTest();
        Assert.Single(failed.Effects);
        Assert.Equal(1, failed.InboxCount);
        Assert.Equal(0, failed.ProcessedMessageCount);
        await attempt.Context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await attempt.Receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForEffectCountAsync(attempt.Receiver, 1);
        Assert.NotEqual(failed.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Empty(recovered.InboxDeadLetters);
    }

    [Fact]
    public async Task LateOutgoingIntent_AfterPreparationCutoff_WaitsForNextCapture()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var durable = context.ActivationServices.GetRequiredKeyedService<IDurableDictionary<Guid, DurableEnvelope>>("test-handler-output");
        var sessions = Fixture.Client.ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var first = new DurableEnvelopeBuilder(sessions, receiver.GetGrainId()).To(receiver.GetGrainId(), "messages/output").WithBody(1).Build();
        var late = new DurableEnvelopeBuilder(sessions, receiver.GetGrainId()).To(receiver.GetGrainId(), "messages/output").WithBody(2).Build();
        await receiver.StageOutputAsync(first);
        using var preparation = outbox.BlockNextPreparation();
        var write = Fixture.WriteStateAsync(receiver).AsTask();
        await preparation.WaitAsync();
        await OnTurnAsync(context, () => outbox.Send(late));
        preparation.Release();
        await write;
        Assert.Equal(first.MessageId, Assert.Single(durable).Key);
        Assert.Equal(new[] { first.MessageId }, outbox.LastCapturedIds);
        Assert.Equal(2, outbox.Count);
        await receiver.RetryWriteStateAsync();
        Assert.Equal(new[] { late.MessageId }, outbox.LastCapturedIds);
        Assert.Equal(2, durable.Count);
        await receiver.RequestDeactivationAsync();
        Assert.Equal(2, (await receiver.GetSnapshotAsync()).OutboxCount);
    }

    [Fact]
    public async Task AcceptanceAfterInboxPreparationCutoff_IsAppliedByLaterOperation()
    {
        var receiver = NewGrain();
        using var seed = CreateEnvelope(receiver, NewMessage(110, "seed"), "messages/cutoff");
        var owner = new DurableJob
        {
            Id = "seed-physical",
            ShardId = "seed-shard",
            Name = ReceiverTestServices.InboxJobName,
            TargetGrainId = receiver.GetGrainId(),
            DueTime = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string> { ["orleans.messaging.ownership-id"] = "seed:1" }
        };
        await receiver.SeedInboxStateAsync(seed.Value, "seed:1", owner);
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        using var preparation = outbox.BlockNextPreparation();
        grain.Captures.Clear();
        var preceding = Fixture.WriteStateAsync(receiver).AsTask();
        await preparation.WaitAsync();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/cutoff");
        using var incoming = CreateEnvelope(receiver, NewMessage(111, "late-acceptance"), "messages/cutoff");
        var extension = (IDurableInboxExtension)context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        Task<DeliveryResult> delivery = null!;
        await OnTurnAsync(context, () => delivery = extension.DeliverAsync(incoming.Value).AsTask());
        Assert.False(delivery.IsCompleted);
        Assert.Equal(1, grain.GetSnapshotForTest().InboxCount);
        preparation.Release();
        await preceding;
        Assert.Equal(DeliveryStatus.Accepted, (await delivery).Status);
        await handler.WaitUntilEnteredAsync();
        Assert.Equal(1, grain.Captures[0].InboxCount);
        Assert.Equal(2, grain.Captures[1].InboxCount);
        Assert.Equal(2, grain.GetSnapshotForTest().InboxCount);
        handler.Release();
        Assert.Equal(2, (await Fixture.WaitForEffectCountAsync(receiver, 2)).ProcessedMessageCount);
    }

    [Fact]
    public async Task CallerWaitCancellation_DoesNotWithdrawCapturedAcceptance()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var storage = Fixture.Storage.BlockWrite(journal);
        using var envelope = CreateEnvelope(receiver, NewMessage(112, "cancel-wait"));
        using var cancellation = new CancellationTokenSource();
        var delivery = DeliverWithCancellationAsync(receiver, envelope.Value, cancellation.Token);
        await storage.WaitUntilEnteredAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        storage.Release();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope.Value)).Status);
    }

    [Fact]
    public async Task CancellationDuringAdmittedHandlerPreparation_FencesAndFreshActivationRetries()
    {
        var receiver = NewGrain();
        using var envelope = CreateEnvelope(receiver, NewMessage(113, "attempt-cancel"), "messages/attempt-cancel");
        var job = new DurableJob
        {
            Id = "cancel-physical",
            ShardId = "cancel-shard",
            Name = ReceiverTestServices.InboxJobName,
            TargetGrainId = receiver.GetGrainId(),
            DueTime = DateTimeOffset.UtcNow,
            Metadata = new Dictionary<string, string> { ["orleans.messaging.ownership-id"] = "cancel:1" }
        };
        await receiver.SeedInboxStateAsync(envelope.Value, "cancel:1", job);
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var extension = (IDurableJobFeatureHandler)context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/attempt-cancel");
        using var cancellation = new CancellationTokenSource();
        DurableJobRunResult result = null!;
        await OnTurnAsync(context, () => result = extension.ExecuteJobAsync(new JobContext(job), cancellation.Token).GetAwaiter().GetResult());
        Assert.Equal(DurableJobRunStatus.InProgress, result.Status);
        await handler.WaitUntilEnteredAsync();
        cancellation.Cancel();
        var failure = await grain.Faulted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Empty(grain.GetSnapshotForTest().Effects);
        Assert.Equal(1, grain.GetSnapshotForTest().InboxCount);
        handler.Release();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(1, recovered.ProcessedMessageCount);
    }

    [Fact]
    public async Task CompositeIsSoleMessagingObserver_AndRequiresExplicitOutboxEndpoint()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var services = Fixture.GetGrainContext(receiver).ActivationServices;
        var manager = services.GetRequiredService<IJournaledStateManager>();
        var registered = (IEnumerable<IJournaledStateObserver>)manager.GetType().GetField("_observers",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
        var compositeType = ReceiverTestServices.GetImplementationType("DurableMessagingJournalObserver");
        Assert.Single(registered, observer => observer.GetType() == compositeType);
        Assert.DoesNotContain(registered, observer => observer.GetType().Name is "DurableInboxExtension" or "JournaledTestOutbox");
        var endpointType = ReceiverTestServices.GetImplementationType("DurableMessagingJournalEndpoint");
        var endpoint = services.GetRequiredKeyedService(endpointType, "__orleans.durable-messaging.outbox-observer");
        Assert.Same(services.GetRequiredService<IDurableOutbox>(), endpointType.GetProperty("Observer")!.GetValue(endpoint));
        var missing = new ServiceCollection();
        missing.AddLogging();
        using var provider = missing.BuildServiceProvider();
        var inbox = services.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        var exception = Assert.Throws<InvalidOperationException>(() => ActivatorUtilities.CreateInstance(provider, compositeType, inbox));
        Assert.Contains("DurableMessagingJournalEndpoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestVetoBeforeAdmission_LeavesAcknowledgedScheduleProposalLocal()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
        grain.NextWriteRejection = new InvalidOperationException("Injected pre-admission veto.");
        using var envelope = CreateEnvelope(receiver, NewMessage(114, "request-veto"));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => DeliverAsync(receiver, envelope.Value));
        Assert.Contains("pre-admission veto", failure.Message, StringComparison.Ordinal);
        var rejected = await receiver.GetSnapshotAsync();
        Assert.Equal(before.ActivationId, rejected.ActivationId);
        Assert.Equal(0, rejected.InboxCount);
        Assert.Null(rejected.InboxJobId);
        Assert.Null(rejected.InboxJob);
        Assert.Empty(rejected.Effects);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
        Assert.False(grain.Faulted.Task.IsCompleted);
        Assert.Single(Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        Assert.Equal(1, Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Count);
        var jobs = Fixture.JobManagerProbe.GetScheduledJobs(ReceiverTestServices.InboxJobName, receiver.GetGrainId());
        Assert.Equal(2, jobs.Count);
        Assert.NotEqual(jobs[0].Metadata!["orleans.messaging.ownership-id"], jobs[1].Metadata!["orleans.messaging.ownership-id"]);
    }

    private sealed class JobContext(DurableJob job) : IJobRunContext
    {
        public DurableJob Job { get; } = job;
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        public int DequeueCount => 1;
    }

    private async Task<Attempt> PrepareAttemptAsync(string routeSuffix)
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var route = "messages/" + routeSuffix;
        var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), route);
        using var envelope = CreateEnvelope(receiver, NewMessage(109, routeSuffix) with { ForwardTo = GrainId.Create("output", routeSuffix) }, route);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var outbox = (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();
        var preparation = outbox.BlockNextPreparation();
        handler.Release();
        await preparation.WaitAsync();
        return new(receiver, context, outbox, handler, preparation, envelope.Value);
    }

    private static Task OnTurnAsync(IGrainContext context, Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }

    private sealed record Attempt(IDurableMessagingTestGrain Receiver, IGrainContext Context, JournaledTestOutbox Outbox,
        HandlerProbe.Barrier Handler, JournaledTestOutbox.PreparationBarrier Preparation, DurableEnvelope Envelope) : IDisposable
    {
        public DurableMessagingTestGrain Grain { get; } = Assert.IsType<DurableMessagingTestGrain>(Context.GrainInstance);
        public IJournaledStateManager Manager => Context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        public JournalId JournalId => JournalId.FromGrainId(Receiver.GetGrainId());
        public void Dispose() { Preparation.Dispose(); Handler.Dispose(); }
    }
}
