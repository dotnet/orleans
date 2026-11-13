using Orleans.Messaging.Tests.Support;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class MessagingKeepAliveTests : MessagingBehaviorTestBase
{
    private const string HeldRoute = "messages/held";

    [Fact]
    public async Task RunningHandler_KeepsActivationAliveUntilInboxDrains()
    {
        var receiver = NewGrain();
        using var handler = Fixture.HandlerProbe.Arm(receiver.GetGrainId(), HeldRoute);
        var envelope = CreateEnvelope(receiver, NewMessage(1, "held"), HeldRoute);
        Assert.Equal(DeliveryStatus.Accepted, (await DeliverAsync(receiver, envelope)).Status);
        await handler.WaitUntilEnteredAsync();
        var context = Fixture.GetGrainContext(receiver);

        // The handler runs outside of any grain request, so only the pump's hold prevents idle collection.
        var management = Fixture.Client.GetGrain<IManagementGrain>(0);
        await management.ForceActivationCollection(TimeSpan.Zero, TestContext.Current.CancellationToken);
        Assert.False(context.Deactivated.IsCompleted);

        handler.Release();
        Assert.Equal("held", Assert.Single((await Fixture.WaitForEffectCountAsync(receiver, 1)).Effects).Value);
        await Fixture.CollectAsync(context);
    }
}
