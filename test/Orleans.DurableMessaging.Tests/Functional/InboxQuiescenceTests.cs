using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
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
public sealed class InboxQuiescenceTests : DurableMessagingBehaviorTestBase
{
    [Fact]
    public async Task AlwaysInterleaveCallback_ObservesAdmittedClearOnSameActivationScheduler()
    {
        var receiver = NewGrain();
        var owner = CreateJob(receiver, ReceiverTestServices.InboxJobName, "owner:1");
        await receiver.SetInboxOwnershipAsync("owner:1", owner);
        await RefreshSeededOwnerAsync(receiver);
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        using var barrier = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        Assert.Equal(DurableJobRunStatus.InProgress, (await InvokeJobAsync(receiver, owner)).Status);
        await barrier.WaitUntilEnteredAsync();
        var scheduler = barrier.EntryScheduler;
        Assert.NotSame(TaskScheduler.Default, scheduler);
        Assert.Same(context, barrier.EntryContext);
        var operation = Assert.Single(GetPending(context).Cast<object>());
        Assert.Equal("ClearOwnerWrite", operation.GetType().Name);
        var finished = (TaskCompletionSource)operation.GetType().GetProperty("Finished")!.GetValue(operation)!;

        Assert.Equal(DurableJobRunStatus.Completed, (await InvokeJobAsync(receiver, CreateJob(receiver, "test/probe-scheduler"))).Status);
        Assert.Same(scheduler, grain.JobScheduler);
        Assert.Same(context, grain.JobGrainContext);
        for (var attempt = 2; attempt <= 4; attempt++)
        {
            Assert.Equal(DurableJobRunStatus.InProgress, (await InvokeJobAsync(receiver, owner, attempt)).Status);
            Assert.Same(operation, Assert.Single(GetPending(context).Cast<object>()));
        }
        Assert.Single(events.Events, item => item.Payload is GrainTimerEvents.Created created && ReferenceEquals(created.GrainContext, context));
        barrier.Release();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(), static snapshot => snapshot.InboxJobId is null);
        _ = await receiver.GetSnapshotAsync();
        Assert.Equal(DurableJobRunStatus.Completed, (await InvokeJobAsync(receiver, CreateJob(receiver, "test/probe-scheduler"))).Status);
        Assert.Same(scheduler, grain.JobScheduler);
        Assert.Same(context, grain.JobGrainContext);
        Assert.Empty(GetPending(context));
        Assert.Equal(DurableJobRunStatus.Completed, (await InvokeJobAsync(receiver, owner, 5)).Status);
        Assert.False(grain.DeactivationFailure.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerDelete_AfterAcknowledgedWork_StopsComponentsAndUsesFreshActivation(bool pump)
    {
        var receiver = NewGrain();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/delete-ack");
        using var first = CreateEnvelope(receiver, NewMessage(160, "first"), "messages/delete-ack");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, first.Value)).Status);
        await handler.WaitUntilEnteredAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var extension = context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        var outbox = GetOutbox(context);
        using var second = CreateEnvelope(receiver, NewMessage(161, "second"), "messages/delete-ack");
        Task<DeliveryResult>? secondDelivery = null;

        if (pump)
        {
            var owner = Assert.IsType<DurableJob>(grain.GetSnapshotForTest().InboxJob);
            Assert.Equal(DurableJobRunStatus.InProgress, (await InvokeJobAsync(receiver, owner)).Status);
        }
        else
        {
            secondDelivery = DeliverAsync(receiver, second.Value);
        }

        handler.Release();
        if (secondDelivery is not null) Assert.Equal(DeliveryStatus.Accepted, (await secondDelivery).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, pump ? 1 : 2);
        Assert.Equal(pump ? 1 : 2, completed.ProcessedMessageCount);
        Assert.False(grain.DeactivationFailure.Task.IsCompleted);
        using var storage = Fixture.Storage.BlockDelete(JournalId.FromGrainId(receiver.GetGrainId()));
        var deletion = receiver.DeleteStateAndDeactivateAsync();
        await storage.WaitUntilEnteredAsync();
        AssertStopped(extension, outbox);
        Assert.False(deletion.IsCompleted);
        storage.Release();
        await deletion;
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var deleted = await receiver.GetSnapshotAsync();
        Assert.NotEqual(completed.ActivationId, deleted.ActivationId);
        Assert.Empty(deleted.Effects);
        Assert.Equal(0, deleted.ProcessedMessageCount);
        Assert.Null(deleted.InboxJobId);
    }

    [Fact]
    public async Task OwnerDelete_WaitsForControlledPreparationBeforeStorageDelete()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var outbox = GetOutbox(context);
        var extension = context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        using var preparation = outbox.BlockNextPreparation(ignoreCancellation: true);
        using var envelope = CreateEnvelope(receiver, NewMessage(162, "application-preparation"), "output/prepared");
        var acquisition = PrepareOnTurnAsync(context, outbox, envelope.Value);
        await preparation.WaitAsync();
        using var storage = Fixture.Storage.BlockDelete(JournalId.FromGrainId(receiver.GetGrainId()));
        var deleteEntered = storage.WaitUntilEnteredAsync();
        var deletion = receiver.DeleteStateAndDeactivateAsync();
        await outbox.Stopping.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(deletion.IsCompleted);
        Assert.False(deleteEntered.IsCompleted);
        Assert.False(preparation.Operation!.Completed.IsCompleted);
        Assert.Equal(0, outbox.SendCalls);
        preparation.Release();
        using (var batch = await acquisition)
        {
            await deleteEntered;
            AssertStopped(extension, outbox);
            Assert.True(preparation.Operation.Completed.IsCompleted);
            var observed = Assert.Single(outbox.PreparedBatches);
            Assert.Equal(envelope.Value.MessageId, Assert.Single(observed.MessageIds));
            Assert.Equal(0, observed.DisposeCalls);
            Assert.Throws<InvalidOperationException>(() => outbox.Send(batch));
        }
        Assert.Equal(1, Assert.Single(outbox.PreparedBatches).DisposeCalls);
        Assert.Empty(grain.GetSnapshotForTest().Effects);
        Assert.Empty(outbox.Messages);
        storage.Release();
        await deletion;
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var fresh = await receiver.GetSnapshotAsync();
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, fresh.ActivationId);
        Assert.Empty(fresh.Effects);
        Assert.Equal(0, fresh.InboxCount);
        Assert.Equal(0, fresh.OutboxCount);
    }

    [Fact]
    public async Task OwnerDelete_DrainsCanceledDeliveryAndGateWaiterBeforeReset()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var extension = (IDurableInboxExtension)context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        using var blocked = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        using var first = CreateEnvelope(receiver, NewMessage(163, "owned-after-cancel"));
        using var second = CreateEnvelope(receiver, NewMessage(164, "gate-waiter"));
        using var cancellation = new CancellationTokenSource();
        var delivery = DeliverWithCancellationAsync(receiver, first.Value, cancellation.Token);
        await blocked.WaitUntilEnteredAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delivery);
        _ = await receiver.GetSnapshotAsync();
        var started = new TaskCompletionSource<Task<DeliveryResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = TestContext.Current.CancellationToken;
        context.Scheduler.QueueAction(() =>
        {
            try { started.SetResult(extension.DeliverAsync(second.Value, token).AsTask()); }
            catch (Exception exception) { started.SetException(exception); }
        });
        var waiting = await started.Task;
        Assert.False(waiting.IsCompleted);
        var outbox = GetOutbox(context);
        using var deletionStorage = Fixture.Storage.BlockDelete(JournalId.FromGrainId(receiver.GetGrainId()));
        var deleteEntered = deletionStorage.WaitUntilEnteredAsync();
        var deletion = receiver.DeleteStateAndDeactivateAsync();
        await outbox.Stopping.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(deletion.IsCompleted);
        Assert.False(deleteEntered.IsCompleted);
        Assert.Equal(0, CancellationCleanupProbe.Field<SemaphoreSlim>(extension, "_gate").CurrentCount);
        Assert.Single(CancellationCleanupProbe.Field<HashSet<string>>(extension, "_pendingOwnershipIds"));
        blocked.Release();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await deleteEntered;
        AssertStopped(extension, outbox);
        Assert.Equal(1, CancellationCleanupProbe.Field<SemaphoreSlim>(extension, "_gate").CurrentCount);
        Assert.Empty(CancellationCleanupProbe.Field<HashSet<string>>(extension, "_pendingOwnershipIds"));
        Assert.Equal(1, grain.GetSnapshotForTest().InboxCount);
        deletionStorage.Release();
        await deletion;
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var fresh = await receiver.GetSnapshotAsync();
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, fresh.ActivationId);
        Assert.Equal(0, fresh.ProcessedMessageCount);
        Assert.Equal(0, fresh.InboxCount);
        Assert.Empty(fresh.Effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryWhileOwnerClearIsAdmitted_WaitsAndAcceptsWithoutFencing(bool interleaved)
    {
        var receiver = NewGrain();
        var owner = CreateJob(receiver, ReceiverTestServices.InboxJobName, "clear-overlap:1");
        await receiver.SetInboxOwnershipAsync("clear-overlap:1", owner);
        await RefreshSeededOwnerAsync(receiver);
        using var incoming = CreateEnvelope(receiver, NewMessage(181, "after-clear"));
        await receiver.SetControlEnvelopeAsync(incoming.Value);
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        using var preparation = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        Assert.Equal(DurableJobRunStatus.InProgress, (await InvokeJobAsync(receiver, owner)).Status);
        await preparation.WaitUntilEnteredAsync();
        Assert.Equal("ClearOwnerWrite", Assert.Single(GetPending(context).Cast<object>()).GetType().Name);
        Task delivery;
        if (interleaved)
        {
            delivery = InvokeJobAsync(receiver, CreateJob(receiver, "test/deliver-envelope"));
            await grain.ControlDeliveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        else
        {
            delivery = DeliverAsync(receiver, incoming.Value);
        }
        Assert.False(delivery.IsCompleted);
        preparation.Release();
        await delivery;
        if (interleaved)
        {
            Assert.Equal(DurableJobRunStatus.Completed, (await (Task<DurableJobRunResult>)delivery).Status);
        }
        else
        {
            Assert.Equal(DeliveryStatus.Accepted, (await (Task<DeliveryResult>)delivery).Status);
        }
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.False(grain.DeactivationFailure.Task.IsCompleted);
        Assert.Equal(1, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerDeleteFailure_DeactivatesAndFreshOwnerObservesActualStorageOutcome(bool committed)
    {
        var receiver = NewGrain();
        var seed = new DurableEffect(Guid.NewGuid(), 1, 188, "preserved-before-delete");
        await receiver.StageEffectAsync(seed);
        await receiver.RetryWriteStateAsync();
        var before = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var extension = context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        var outbox = GetOutbox(context);
        var journalId = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journalId);
        using var storage = Fixture.Storage.BlockDelete(journalId);
        if (committed) Fixture.Storage.FailAfterDelete(journalId);
        var deletion = receiver.DeleteStateAndDeactivateAsync();
        await storage.WaitUntilEnteredAsync();
        AssertStopped(extension, outbox);
        if (committed) storage.Release();
        else storage.Fail();
        var failure = await Assert.ThrowsAsync<IOException>(() => deletion);
        var reason = await grain.DeactivationFailure.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.IsType<IOException>(reason);
        Assert.Equal(failure.Message, reason.Message);
        Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journalId));
        Assert.Equal(seed, Assert.Single(grain.GetSnapshotForTest().Effects));
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        if (committed)
        {
            Assert.Empty(recovered.Effects);
        }
        else
        {
            Assert.Equal(seed, Assert.Single(recovered.Effects));
        }
        Assert.Equal(0, recovered.InboxCount);
        Assert.Equal(0, recovered.ProcessedMessageCount);
    }

    [Fact]
    public async Task OwnerDelete_DiscardsStagedOutputAndLaterDeliveryUsesFreshOwner()
    {
        var receiver = NewGrain();
        await receiver.StageEffectAsync(new DurableEffect(Guid.NewGuid(), 1, 190, "delete-this"));
        await receiver.RetryWriteStateAsync();
        var before = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var outbox = GetOutbox(context);
        using var staged = CreateEnvelope(receiver, NewMessage(193, "staged-output"), "output/staged");
        using var prepared = CreateEnvelope(receiver, NewMessage(194, "prepared-output"), "output/prepared");
        var batch = await PrepareOnTurnAsync(context, outbox, prepared.Value);
        try
        {
            await receiver.StageOutputAsync(staged.Value);
            Assert.Equal(staged.Value.MessageId, Assert.Single(outbox.Messages).MessageId);
            var journal = JournalId.FromGrainId(receiver.GetGrainId());
            var writes = Fixture.Storage.GetSuccessfulWriteCount(journal);
            await receiver.DeleteStateAndDeactivateAsync();
            await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(writes, Fixture.Storage.GetSuccessfulWriteCount(journal));
            Assert.Empty(outbox.Messages);
            Assert.Empty(grain.GetSnapshotForTest().Effects);
            Assert.True(outbox.Stopping.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => outbox.Send(batch));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await outbox.PrepareSendAsync([prepared.Value], TestContext.Current.CancellationToken));
            Assert.Equal(0, outbox.PreparedBatches[0].DisposeCalls);
        }
        finally
        {
            batch.Dispose();
        }
        Assert.Equal(1, outbox.PreparedBatches[0].DisposeCalls);
        batch.Dispose();
        Assert.Equal(2, outbox.PreparedBatches[0].DisposeCalls);
        Assert.Empty(outbox.Messages);
        var fresh = await receiver.GetSnapshotAsync();
        Assert.NotEqual(before.ActivationId, fresh.ActivationId);
        Assert.Empty(fresh.Effects);
        Assert.Equal(0, fresh.OutboxCount);
        using var envelope = CreateEnvelope(receiver, NewMessage(191, "new-state"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        var after = await Fixture.WaitForEffectCountAsync(receiver, 1);
        _ = await receiver.GetSnapshotAsync();
        Assert.Equal(fresh.ActivationId, after.ActivationId);
        Assert.Equal("new-state", Assert.Single(after.Effects).Value);
        Assert.Equal(1, after.ProcessedMessageCount);
        Assert.False(grain.DeactivationFailure.Task.IsCompleted);
    }

    private static JournaledTestOutbox GetOutbox(IGrainContext context) =>
        (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();

    private static void AssertStopped(object extension, JournaledTestOutbox outbox)
    {
        Assert.True(outbox.Stopping.IsCompleted);
        Assert.Equal(outbox.PreparationsStarted, outbox.PreparationsCompleted);
        Assert.False(CancellationCleanupProbe.CoordinatorIsActive(extension));
        Assert.Empty(CancellationCleanupProbe.Field<IList>(extension, "_pendingWrites"));
        Assert.Equal(0, CancellationCleanupProbe.Field<int>(extension, "_metricsActive"));
        Assert.Equal(0, CancellationCleanupProbe.Field<int>(extension, "_reportedDepth"));
        var results = CancellationCleanupProbe.Field<object>(extension, "_pumpResults");
        var entries = (IDictionary)results.GetType().GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(results)!;
        Assert.DoesNotContain(entries.Keys.Cast<object>(),
            key => (string)key.GetType().GetProperty("JobName")!.GetValue(key)! == ReceiverTestServices.InboxJobName);
    }

    private static Task<IPreparedOutboxBatch> PrepareOnTurnAsync(IGrainContext context, JournaledTestOutbox outbox, DurableEnvelope envelope)
    {
        var started = new TaskCompletionSource<Task<IPreparedOutboxBatch>>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { started.SetResult(outbox.PrepareSendAsync([envelope], TestContext.Current.CancellationToken).AsTask()); }
            catch (Exception exception) { started.SetException(exception); }
        });
        return started.Task.Unwrap();
    }

    private static IList GetPending(IGrainContext context)
    {
        var type = ReceiverTestServices.GetImplementationType("DurableInboxExtension");
        return (IList)type.GetField("_pendingWrites", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(context.ActivationServices.GetRequiredService(type))!;
    }

    private static DurableJob CreateJob(IDurableMessagingTestGrain receiver, string name, string? ownershipId = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ShardId = "quiescence-tests",
        Name = name,
        TargetGrainId = receiver.GetGrainId(),
        DueTime = DateTimeOffset.UtcNow,
        Metadata = ownershipId is null ? null : new Dictionary<string, string> { ["orleans.messaging.ownership-id"] = ownershipId }
    };

    private static async Task<DurableJobRunResult> InvokeJobAsync(IDurableMessagingTestGrain receiver, DurableJob job, int dequeueCount = 1)
    {
        var assembly = typeof(DurableJob).Assembly;
        var type = assembly.GetType("Orleans.DurableJobs.IDurableJobReceiverExtension", throwOnError: true)!;
        var reference = receiver.AsReference(type);
        var run = Activator.CreateInstance(assembly.GetType("Orleans.DurableJobs.JobRunContext", throwOnError: true)!,
            job, Guid.NewGuid().ToString("N"), dequeueCount)!;
        return await (ValueTask<DurableJobRunResult>)type.GetMethod("HandleDurableJobAsync")!
            .Invoke(reference, [run, TestContext.Current.CancellationToken])!;
    }
}
