using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableMessaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Functional;

[Collection(DurableMessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableMessaging")]
public sealed class ArcMessagingOwnershipTests : DurableMessagingBehaviorTestBase
{
    private static readonly FieldInfo References = typeof(ArcBufferPage).GetField("_refCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static int Pins(ArcBufferPage page) => (int)References.GetValue(page)!;

    [Fact]
    public async Task DirectAdmission_CallerCancellationAndDisposalDoNotEndActualAcceptanceOrHandlerBorrow()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<DurableMessagingTestGrain>(context.GrainInstance);
        var extension = (IDurableInboxExtension)context.ActivationServices.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        using var caller = CreateEnvelope(receiver, NewMessage(300, "owned-admission"));
        using var observer = caller.Value.Retain();
        var page = observer.Payload.First;
        var expected = observer.Payload.ToArray();
        Assert.Equal(2, Pins(page)); // caller and test observer, no durable owner yet
        using var handler = new BorrowProbe(page, expected);
        await OnTurnAsync(context, () => grain.HandlerOverride = handler);
        using var scheduling = Fixture.JobManagerProbe.BlockNext(ReceiverTestServices.InboxJobName);
        using var cancellation = new CancellationTokenSource();
        Task<DeliveryResult> waiting = null!;
        await OnTurnAsync(context, () => waiting = extension.DeliverAsync(caller.Value, cancellation.Token).AsTask());
        await scheduling.WaitUntilEnteredAsync();
        var actual = (Task)extension.GetType().GetField("_activeDelivery", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(extension)!;
        Assert.Equal(3, Pins(page)); // temporary admission owner was acquired before scheduling
        caller.Dispose();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(actual.IsCompleted);
        Assert.Equal(2, Pins(page)); // cancellation did not release the actual acceptance owner
        scheduling.Continue();
        await actual.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(3, Pins(page)); // observer, durable dictionary, actual handler operation
        handler.Release.TrySetResult();
        await handler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var completed = await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(), s => s.InboxCount == 0 && s.ProcessedMessageCount == 1);
        Assert.Empty(completed.InboxDeadLetters);
        await receiver.RequestDeactivationAsync();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, Pins(page)); // only the explicitly retained test observer remains
        Assert.Equal(expected, observer.Payload.ToArray());
    }

    [Fact]
    public async Task RpcAcceptanceAndDuplicate_RequestDisposalNeverConsumesCallerOwner()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        using var caller = CreateEnvelope(receiver, NewMessage(302, "rpc-owned-success"));
        var page = caller.Value.Payload.First;
        var expected = caller.Value.Payload.ToArray();
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, caller.Value)).Status);
        await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, caller.Value)).Status);
        await receiver.RequestDeactivationAsync();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, Pins(page));
        Assert.Equal(expected, caller.Value.Payload.ToArray());
        _ = await receiver.GetSnapshotAsync();
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, caller.Value)).Status);
    }

    [Fact]
    public async Task RpcRejection_ReleasesRequestOwnerAndPreservesCallerOwner()
    {
        var receiver = NewGrain();
        await receiver.ConfigureHandlerAsync(false);
        var original = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await original.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        using var caller = CreateEnvelope(receiver, NewMessage(301, "rejected-owner"));
        var page = caller.Value.Payload.First;
        var expected = caller.Value.Payload.ToArray();
        Assert.Equal(DeliveryStatus.HandlerNotFound, (await DeliverAsync(receiver, caller.Value)).Status);
        await receiver.RequestDeactivationAsync();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(1, Pins(page));
        Assert.Equal(expected, caller.Value.Payload.ToArray());
    }

    private static Task OnTurnAsync(IGrainContext context, Action action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Scheduler.QueueAction(() =>
        {
            try { action(); completed.SetResult(); }
            catch (Exception exception) { completed.SetException(exception); }
        });
        return completed.Task;
    }

    private sealed class BorrowProbe(ArcBufferPage page, byte[] expected) : IInboxHandler, IDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            try
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                Assert.Equal(3, Pins(page));
                context.Complete();
                Assert.Equal(2, Pins(page)); // dictionary was removed; actual handler still borrows its own owner
                Assert.Equal(expected, context.Envelope.Payload.ToArray());
                Completed.TrySetResult();
            }
            catch (Exception exception)
            {
                Completed.TrySetException(exception);
                throw;
            }
        }
        public void Dispose() => Release.TrySetResult();
    }
}
