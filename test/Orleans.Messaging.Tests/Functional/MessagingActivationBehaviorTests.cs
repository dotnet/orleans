using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class MessagingActivationBehaviorTests : MessagingBehaviorTestBase
{
    public MessagingActivationBehaviorTests() : base(receiverOnly: false)
    {
    }

    [Fact]
    public async Task ApplicationJournaledStateNamesDoNotCollideWithMessagingState()
    {
        var grain = NewGrain();
        _ = await grain.GetSnapshotAsync();
        var services = Fixture.GetGrainContext(grain).ActivationServices;
        var manager = services.GetRequiredService<IJournaledStateManager>();

        // The test grain registers an application state named "inbox", the messaging state's unprefixed name.
        Assert.True(manager.TryGetStateMachine("inbox", out var applicationState));
        Assert.IsAssignableFrom<IDurableValue<string>>(applicationState);
        Assert.True(manager.TryGetStateMachine(MessagingStateNames.Inbox, out var inboxState));
        Assert.Same(services.GetRequiredService<Inbox>(), inboxState);
        Assert.NotSame(inboxState, applicationState);
    }
    [Fact]
    public async Task PublicHosting_BindsRealOutboxAndRegisteredMessagingStates()
    {
        var grain = NewGrain();
        _ = await grain.GetSnapshotAsync();
        var services = Fixture.GetGrainContext(grain).ActivationServices;
        var outbox = services.GetRequiredService<Outbox>();
        var inbox = services.GetRequiredService<Inbox>();
        Assert.Same(outbox, services.GetRequiredService<IOutbox>());
        Assert.Same(inbox, services.GetRequiredService<IInbox>());
        var manager = services.GetRequiredService<IJournaledStateManager>();
        Assert.True(manager.TryGetStateMachine(MessagingStateNames.Outbox, out var outboxState));
        Assert.True(manager.TryGetStateMachine(MessagingStateNames.Inbox, out var inboxState));
        Assert.Same(outbox, outboxState);
        Assert.Same(inbox, inboxState);
    }

}
