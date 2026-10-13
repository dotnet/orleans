using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class InboxLifecycleBehaviorTests : MessagingBehaviorTestBase
{
    [Fact]
    public async Task Deactivation_StopsPumpAndWaitsForActiveHandler()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var shutdown = Fixture.GetPump(receiver).ShutdownToken;
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/teardown-active", observeCancellation: false);
        var envelope = CreateEnvelope(receiver, NewMessage(95, "active-handler"), "messages/teardown-active");
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await handler.WaitUntilEnteredAsync();
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = shutdown.Register(stopping.SetResult);

        context.Deactivate(new(DeactivationReasonCode.ApplicationRequested, "Verify active handler teardown."), TestContext.Current.CancellationToken);

        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(context.Deactivated.IsCompleted);
        handler.Release();
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var recovered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(0, recovered.InboxCount);
    }

    [Fact]
    public async Task DeactivationBeforeProcessing_RecoveryDrainsAcceptedMessageOnce()
    {
        var receiver = NewGrain();
        var before = await receiver.GetSnapshotAsync();
        var context = Fixture.GetGrainContext(receiver);
        var envelope = CreateEnvelope(receiver, NewMessage(96, "queued"), "messages/queued");

        // Deactivation cancels any handler the original activation starts, leaving the message to recovery.
        using (Fixture.HandlerProbe.Arm(receiver.GetGrainId(), "messages/queued"))
        {
            Assert.Equal(DeliveryStatus.Accepted, (await receiver.AcceptAndDeactivateAsync(envelope)).Status);
            await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        _ = await receiver.GetSnapshotAsync();
        var recovered = await Fixture.WaitForEffectCountAsync(receiver, 1);
        Assert.NotEqual(before.ActivationId, recovered.ActivationId);
        Assert.Equal(1, Assert.Single(recovered.Effects).Count);
        Assert.Equal(0, recovered.InboxCount);
    }
}
