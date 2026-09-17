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
        using var barrier = outbox.BlockNextPreparation();
        using var events = new DiagnosticEventCollector(GrainTimerEvents.ListenerName);
        Assert.Equal(DurableJobRunStatus.InProgress, (await InvokeJobAsync(receiver, owner)).Status);
        await barrier.WaitAsync();
        var scheduler = outbox.PreparationScheduler;
        Assert.NotSame(TaskScheduler.Default, scheduler);
        Assert.Same(context, outbox.PreparationContext);
        var admitted = GetAdmitted(context);
        Assert.Equal("ClearOwnerWrite", Assert.Single(admitted.Cast<object>()).GetType().Name);

        Assert.Equal(DurableJobRunStatus.Completed, (await InvokeJobAsync(receiver, CreateJob(receiver, "test/probe-scheduler"))).Status);
        Assert.Same(scheduler, grain.JobScheduler);
        Assert.Same(context, grain.JobGrainContext);
        for (var attempt = 2; attempt <= 4; attempt++)
        {
            Assert.Equal(DurableJobRunStatus.InProgress, (await InvokeJobAsync(receiver, owner, attempt)).Status);
            Assert.Same(admitted, GetAdmitted(context));
        }
        Assert.Single(events.Events, item => item.Payload is GrainTimerEvents.Created created && ReferenceEquals(created.GrainContext, context));
        barrier.Release();
        await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(), static snapshot => snapshot.InboxJobId is null);
        _ = await receiver.GetSnapshotAsync();
        Assert.Same(scheduler, outbox.ContinuationScheduler);
        Assert.Same(context, outbox.ContinuationContext);
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
                Assert.Empty(GetAdmitted(context));
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
        using var blocked = GetOutbox(context).BlockNextPreparation();
        using var first = CreateEnvelope(receiver, NewMessage(163, "owned-after-cancel"));
        using var second = CreateEnvelope(receiver, NewMessage(164, "gate-waiter"));
        using var cancellation = new CancellationTokenSource();
        var delivery = DeliverWithCancellationAsync(receiver, first.Value, cancellation.Token);
        await blocked.WaitAsync();
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

    private static JournaledTestOutbox GetOutbox(IGrainContext context) =>
        (JournaledTestOutbox)context.ActivationServices.GetRequiredService<IDurableOutbox>();

    private static Array GetAdmitted(IGrainContext context)
    {
        var type = ReceiverTestServices.GetImplementationType("DurableInboxExtension");
        return (Array)type.GetField("_admittedWrites", BindingFlags.Instance | BindingFlags.NonPublic)!
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
