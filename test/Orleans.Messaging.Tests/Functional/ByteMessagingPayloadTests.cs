using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class ByteMessagingPayloadTests : MessagingBehaviorTestBase
{
    [Fact]
    public async Task RpcAcceptanceAndDuplicate_CopyPayloadAndPreserveCallerBytesAcrossReplay()
    {
        var receiver = NewGrain();
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var grain = Assert.IsType<MessagingTestGrain>(context.GrainInstance);
        var envelope = CreateEnvelope(receiver, NewMessage(302, "rpc-bytes"));
        var expected = envelope.Envelope.Payload.ToArray();
        using var handler = new PayloadProbe(expected);
        await OnTurnAsync(context, () => grain.HandlerOverride = handler);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotSame(envelope.Envelope.Payload, handler.Payload);
        envelope.Envelope.Payload[0] ^= 0xff;
        Assert.Equal(expected, handler.Payload.ToArray());
        envelope.Envelope.Payload[0] ^= 0xff;
        Assert.Equal(expected, envelope.Envelope.Payload.ToArray());
        handler.Release.TrySetResult();
        await handler.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await Fixture.SnapshotProbe.WaitAsync(receiver.GetGrainId(),
            snapshot => snapshot.InboxCount == 0 && snapshot.ProcessedMessageCount == 1);
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
        await receiver.RequestDeactivationAsync();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(expected, envelope.Envelope.Payload.ToArray());
        _ = await receiver.GetSnapshotAsync();
        Assert.Equal(DeliveryStatus.Duplicate, (await DeliverAsync(receiver, envelope)).Status);
    }

    [Fact]
    public async Task RpcRejection_PreservesCallerBytesWithoutStagingState()
    {
        var receiver = NewGrain();
        await receiver.ConfigureHandlerAsync(false);
        var original = Fixture.GetGrainContext(receiver);
        await receiver.RequestDeactivationAsync();
        await original.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        _ = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var envelope = CreateEnvelope(receiver, NewMessage(301, "rejected-bytes"));
        var expected = envelope.Envelope.Payload.ToArray();
        Assert.Equal(DeliveryStatus.HandlerNotFound, (await DeliverAsync(receiver, envelope)).Status);
        var rejected = await receiver.GetSnapshotAsync();
        Assert.Equal(0, rejected.InboxCount);
        Assert.Equal(0, rejected.ProcessedMessageCount);
        Assert.Empty(rejected.Effects);
        await receiver.RequestDeactivationAsync();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(expected, envelope.Envelope.Payload.ToArray());
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

    private sealed class PayloadProbe(byte[] expected) : IInboxHandler, IDisposable
    {
        public byte[] Payload { get; private set; } = null!;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanHandle(IInboxHandlerContext context) => true;


        public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            try
            {
                Payload = context.Message.Envelope.Payload;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                Assert.Equal(expected, Payload.ToArray());
                context.Complete();
                Assert.Same(Payload, context.Message.Envelope.Payload);
                Assert.Equal(expected, context.Message.Envelope.Payload.ToArray());
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
