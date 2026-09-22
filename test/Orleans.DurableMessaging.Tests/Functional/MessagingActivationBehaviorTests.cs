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
public sealed class MessagingActivationBehaviorTests : DurableMessagingBehaviorTestBase
{
    public MessagingActivationBehaviorTests() : base(receiverOnly: false)
    {
    }

    [Fact]
    public void DefaultHosting_UsesBinaryJournalFormat()
    {
        Assert.Equal("orleans-binary", Fixture.Storage.JournalFormatKey);
    }

    [Fact]
    public async Task ApplicationJournaledStateNamesDoNotCollideWithMessagingState()
    {
        var grain = NewGrain();

        var snapshot = await grain.GetSnapshotAsync();

        Assert.Equal(0, snapshot.InboxCount);
        Assert.Equal(0, snapshot.OutboxCount);
    }
    [Fact]
    public async Task PublicHosting_BindsRealOutboxAndRegisteredMessagingStates()
    {
        var grain = NewGrain();
        _ = await grain.GetSnapshotAsync();
        var services = Fixture.GetGrainContext(grain).ActivationServices;
        var outbox = services.GetRequiredService<IDurableOutbox>();
        Assert.Equal("Orleans.DurableMessaging.DurableOutbox", outbox.GetType().FullName);
        Assert.Same(outbox, services.GetRequiredKeyedService<IDurableOutbox>("__orleans.durable-messaging.outbox"));
        var manager = services.GetRequiredService<IJournaledStateManager>();
        Assert.True(manager.TryGetStateMachine("__orleans.durable-messaging.outbox", out var outboxState));
        Assert.True(manager.TryGetStateMachine("__orleans.durable-messaging.inbox", out var inboxState));
        Assert.Same(
            services.GetRequiredKeyedService<IDurableDictionary<Guid, DurableEnvelope>>("__orleans.durable-messaging.outbox"),
            outboxState);
        Assert.Same(
            services.GetRequiredKeyedService<IDurableDictionary<(GrainId, Guid), DurableEnvelope>>("__orleans.durable-messaging.inbox"),
            inboxState);
        Assert.NotSame(inboxState, outboxState);
        Assert.Equal(typeof(IJournaledStateManager).Assembly, inboxState.GetType().Assembly);
        Assert.Equal(typeof(IJournaledStateManager).Assembly, outboxState.GetType().Assembly);
    }

}
