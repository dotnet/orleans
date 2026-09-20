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
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var outbox = GetOutbox(context);
        using var barrier = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        TaskScheduler? continuationScheduler = null;
        IGrainContext? continuationContext = null;
        outbox.AfterWriteCompleted = () =>
        {
            continuationScheduler = TaskScheduler.Current;
            continuationContext = ReceiverTestServices.CurrentGrainContext;
        };
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        Assert.Equal(DurableJobRunStatus.InProgress, (await InvokeJobAsync(receiver, owner)).Status);
        await barrier.WaitUntilEnteredAsync();
        var scheduler = barrier.EntryScheduler;
        Assert.NotSame(TaskScheduler.Default, scheduler);
        Assert.Same(context, barrier.EntryContext);
        var admitted = GetAdmitted(context);
        Assert.Empty(GetStaged(context));
        Assert.Equal("ClearOwnerWrite", Assert.Single(admitted.Cast<object>()).GetType().Name);

        Assert.Equal(DurableJobRunStatus.Completed, (await InvokeJobAsync(receiver, CreateJob(receiver, "test/probe-scheduler"))).Status);
        Assert.Same(scheduler, grain.JobScheduler);
        Assert.Same(context, grain.JobGrainContext);
        for (var attempt = 2; attempt <= 4; attempt++)
        {
            Assert.Equal(DurableJobRunStatus.InProgress, (await InvokeJobAsync(receiver, owner, attempt)).Status);
            Assert.Same(admitted, GetAdmitted(context));
            Assert.Empty(GetStaged(context));
        }
        Assert.Single(events.Events, item => item.Payload is GrainTimerEvents.Created created && ReferenceEquals(created.GrainContext, context));
        barrier.Release();
        await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(), static snapshot => snapshot.InboxJobId is null);
        _ = await receiver.GetSnapshotAsync();
        Assert.Same(scheduler, continuationScheduler);
        Assert.Same(context, continuationContext);
        Assert.Empty(GetStaged(context));
        Assert.Empty(GetAdmitted(context));
        Assert.Equal(DurableJobRunStatus.Completed, (await InvokeJobAsync(receiver, owner, 5)).Status);
        Assert.False(grain.Faulted.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteFromAcknowledgementCallback_RejectsContinuingOwner(bool pump)
    {
        var receiver = NewGrain();
        using var first = CreateEnvelope(receiver, NewMessage(160, "first"), "messages/delete-ack");
        var owner = CreateJob(receiver, ReceiverTestServices.InboxJobName, "ack:1");
        await receiver.SeedInboxStateAsync(first.Value, "ack:1", owner);
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var outbox = GetOutbox(context);
        var attempted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = TestContext.Current.CancellationToken;
        outbox.AfterWriteCompleted = () =>
        {
            outbox.AfterWriteCompleted = null;
            try
            {
                Assert.Same(context, ReceiverTestServices.CurrentGrainContext);
                Assert.Empty(GetStaged(context));
                attempted.SetResult(manager.DeleteStateAsync(cancellation).AsTask());
            }
            catch (Exception exception)
            {
                attempted.SetException(exception);
            }
        };

        if (pump)
        {
            Assert.Equal(DurableJobRunStatus.InProgress, (await InvokeJobAsync(receiver, owner)).Status);
        }
        else
        {
            using var second = CreateEnvelope(receiver, NewMessage(161, "second"), "messages/delete-ack");
            Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, second.Value)).Status);
        }

        var delete = await attempted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => delete);
        Assert.Contains("quiescent", failure.Message, StringComparison.Ordinal);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, pump ? 1 : 2);
        Assert.Equal(pump ? 1 : 2, completed.ProcessedMessageCount);
        Assert.False(grain.Faulted.Task.IsCompleted);
        _ = await receiver.GetSnapshotAsync();
        Assert.Equal(DurableJobRunStatus.Completed, (await InvokeJobAsync(receiver, CreateJob(receiver, "test/delete-journal"))).Status);
        var deleted = await receiver.GetSnapshotAsync();
        Assert.Empty(deleted.Effects);
        Assert.Equal(0, deleted.ProcessedMessageCount);
        Assert.Null(deleted.InboxJobId);
    }

    [Fact]
    public async Task InterleavedDeleteDuringHandler_RejectsWithoutPoisoningAdmittedHandler()
    {
        var receiver = NewGrain();
        using var barrier = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/interleaved-delete");
        using var envelope = CreateEnvelope(receiver, NewMessage(162, "handler"), "messages/interleaved-delete");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        await barrier.WaitUntilEnteredAsync();
        var grain = Assert.IsType<DurableMessagingTestGrain>(Fixture.GetGrainContext(receiver).GrainInstance);

        var rejected = await InvokeJobAsync(receiver, CreateJob(receiver, "test/delete-journal"));

        Assert.Equal(DurableJobRunStatus.Failed, rejected.Status);
        Assert.IsType<InvalidOperationException>(rejected.Exception);
        Assert.False(grain.Faulted.Task.IsCompleted);
        barrier.Release();
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(1, Assert.Single(completed.Effects).Count);
        Assert.Equal(1, completed.ProcessedMessageCount);
        Assert.False(grain.Faulted.Task.IsCompleted);
    }

    [Fact]
    public async Task CanceledDeliveryAndGateWaiter_RejectInterleavedDeleteUntilDrained()
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

        var rejected = await InvokeJobAsync(receiver, CreateJob(receiver, "test/delete-journal"));

        Assert.Equal(DurableJobRunStatus.Failed, rejected.Status);
        Assert.Contains("quiescent", Assert.IsType<InvalidOperationException>(rejected.Exception).Message, StringComparison.Ordinal);
        Assert.False(waiting.IsCompleted);
        Assert.False(grain.Faulted.Task.IsCompleted);
        blocked.Release();
        Assert.Equal(DeliveryStatus.Accepted, (await waiting).Status);
        var completed = await Fixture.WaitForEffectCountAsync(receiver, 2);
        Assert.Equal(2, completed.ProcessedMessageCount);
        await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(), static snapshot => snapshot.InboxJobId is null);
        _ = await receiver.GetSnapshotAsync();
        Assert.Equal(DurableJobRunStatus.Completed, (await InvokeJobAsync(receiver, CreateJob(receiver, "test/delete-journal"))).Status);
        Assert.Equal(0, (await receiver.GetSnapshotAsync()).ProcessedMessageCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryWhileOwnerClearIsAdmitted_WaitsAndAcceptsWithoutFencing(bool interleaved)
    {
        var receiver = NewGrain();
        var owner = CreateJob(receiver, ReceiverTestServices.InboxJobName, "clear-overlap:1");
        await receiver.SetInboxOwnershipAsync("clear-overlap:1", owner);
        using var incoming = CreateEnvelope(receiver, NewMessage(181, "after-clear"));
        await receiver.SetControlEnvelopeAsync(incoming.Value);
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        using var preparation = Fixture.Storage.BlockWrite(JournalId.FromGrainId(receiver.GetGrainId()));
        Assert.Equal(DurableJobRunStatus.InProgress, (await InvokeJobAsync(receiver, owner)).Status);
        await preparation.WaitUntilEnteredAsync();
        Assert.Equal("ClearOwnerWrite", Assert.Single(GetAdmitted(context).Cast<object>()).GetType().Name);
        Assert.Empty(GetStaged(context));
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
        Assert.False(grain.Faulted.Task.IsCompleted);
        Assert.Equal(1, Fixture.JobManagerProbe.GetAttemptCount(ReceiverTestServices.InboxJobName, receiver.GetGrainId()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryBeforeDeleteCompletes_ViolatesQuiescenceAndFailsClosed(bool existingOwner)
    {
        var receiver = NewGrain();
        using var seed = CreateEnvelope(receiver, NewMessage(188, "preserved-before-delete"));
        if (existingOwner)
        {
            var owner = CreateJob(receiver, ReceiverTestServices.InboxJobName, "delete-order:1");
            await receiver.SeedInboxStateAsync(seed.Value, "delete-order:1", owner);
        }
        else
        {
            _ = await receiver.GetSnapshotAsync();
        }
        await receiver.RetryWriteStateAsync();
        using var incoming = CreateEnvelope(receiver, NewMessage(189, "invalid-delete-order"));
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var manager = context.ActivationServices.GetRequiredService<IJournaledStateManager>();
        var extension = (IDurableInboxExtension)context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        var outbox = GetOutbox(context);
        var started = new TaskCompletionSource<(Task Delete, Task<DeliveryResult> Delivery)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduling = existingOwner ? null : Fixture.JobManagerProbe.BlockNext(ReceiverTestServices.InboxJobName);
        var token = TestContext.Current.CancellationToken;
        outbox.AfterWriteCompleted = () =>
        {
            outbox.AfterWriteCompleted = null;
            try
            {
                Assert.Same(context, ReceiverTestServices.CurrentGrainContext);
                var delete = manager.DeleteStateAsync(token).AsTask();
                var delivery = extension.DeliverAsync(incoming.Value, token).AsTask();
                started.SetResult((delete, delivery));
            }
            catch (Exception exception) { started.SetException(exception); }
        };
        var journalId = JournalId.FromGrainId(receiver.GetGrainId());
        var writes = Fixture.Storage.GetSuccessfulWriteCount(journalId);
        context.ActivationServices.GetRequiredKeyedService<IDurableValue<string>>("inbox").Value = "delete-overlap";
        await receiver.RetryWriteStateAsync();
        var operations = await started.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        if (scheduling is not null)
        {
            await scheduling.WaitUntilEnteredAsync();
        }
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => operations.Delete);
        Assert.Contains("quiescent", failure.Message, StringComparison.Ordinal);
        Assert.Same(failure, await grain.Faulted.Task);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => operations.Delivery));
        Assert.Equal(writes + 1, Fixture.Storage.GetSuccessfulWriteCount(journalId));
        Assert.Empty(grain.GetSnapshotForTest().Effects);
        Assert.Equal(existingOwner ? 2 : 0, grain.GetSnapshotForTest().InboxCount);
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), token);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(grain.GetSnapshotForTest().ActivationId, recovered.ActivationId);
        if (existingOwner)
        {
            var handled = await Fixture.WaitForEffectCountAsync(receiver, 1);
            Assert.Equal("preserved-before-delete", Assert.Single(handled.Effects).Value);
        }
        else
        {
            Assert.Empty(recovered.Effects);
            Assert.Equal(0, recovered.InboxCount);
        }
    }

    [Fact]
    public async Task AwaitedDeleteThenDirectDelivery_UsesResetStateWithoutFencing()
    {
        var receiver = NewGrain();
        await receiver.StageEffectAsync(new DurableEffect(Guid.NewGuid(), 1, 190, "delete-this"));
        await receiver.RetryWriteStateAsync();
        var before = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        using var envelope = CreateEnvelope(receiver, NewMessage(191, "new-state"));
        Assert.Equal(DeliveryStatus.Accepted, (await receiver.DeleteJournalThenDeliverAsync(envelope.Value)).Status);
        var after = await Fixture.WaitForEffectCountAsync(receiver, 1);
        _ = await receiver.GetSnapshotAsync();
        Assert.Equal(before.ActivationId, after.ActivationId);
        Assert.Equal("new-state", Assert.Single(after.Effects).Value);
        Assert.Equal(1, after.ProcessedMessageCount);
        Assert.False(grain.Faulted.Task.IsCompleted);
    }

    [Fact]
    public async Task RejectedDeleteRequest_LeavesInboxAvailableForDelivery()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        grain.NextDeleteRejection = new InvalidOperationException("Injected delete request veto.");
        var rejected = await InvokeJobAsync(receiver, CreateJob(receiver, "test/delete-journal"));
        Assert.Equal(DurableJobRunStatus.Failed, rejected.Status);
        Assert.Contains("delete request veto", Assert.IsType<InvalidOperationException>(rejected.Exception).Message, StringComparison.Ordinal);
        Assert.False(grain.Faulted.Task.IsCompleted);
        using var envelope = CreateEnvelope(receiver, NewMessage(192, "after-veto"));
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope.Value)).Status);
        Assert.Equal(1, Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Count);
    }

    private static JournaledTestOutbox GetOutbox(IGrainContext context) =>
        (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();

    private static IList GetAdmitted(IGrainContext context)
    {
        var type = ReceiverTestServices.GetImplementationType("DurableInboxExtension");
        return (IList)type.GetField("_admittedWrites", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(context.ActivationServices.GetRequiredService(type))!;
    }

    private static IList GetStaged(IGrainContext context)
    {
        var type = ReceiverTestServices.GetImplementationType("DurableInboxExtension");
        return (IList)type.GetField("_stagedWrites", BindingFlags.Instance | BindingFlags.NonPublic)!
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
