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
    public async Task PublicHosting_BindsRealOutboxEndpointAndRegistersOneComposite()
    {
        var grain = NewGrain();
        _ = await grain.GetSnapshotAsync();
        var services = Fixture.GetGrainContext(grain).ActivationServices;
        var outbox = services.GetRequiredService<IDurableOutbox>();
        Assert.Equal("Orleans.DurableMessaging.DurableOutbox", outbox.GetType().FullName);
        Assert.Same(outbox, services.GetRequiredKeyedService<IDurableOutbox>("__orleans.durable-messaging.outbox"));
        var endpointType = ReceiverTestServices.GetImplementationType("DurableMessagingJournalEndpoint");
        var endpoint = services.GetRequiredKeyedService(endpointType, "__orleans.durable-messaging.outbox-observer");
        Assert.Same(outbox, endpointType.GetProperty("Observer")!.GetValue(endpoint));
        Assert.Equal(typeof(void), endpointType.GetMethod("FinalizeWrite")!.ReturnType);
        var manager = services.GetRequiredService<IJournaledStateManager>();
        var observers = (IEnumerable<IJournaledStateObserver>)manager.GetType().GetField("_observers",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
        Assert.Single(observers, observer => observer.GetType().Name == "DurableMessagingJournalObserver");
        Assert.DoesNotContain(observers, observer => observer.GetType().Name is "DurableInboxExtension" or "DurableOutbox");
        var missing = new ServiceCollection();
        missing.AddLogging();
        using var provider = missing.BuildServiceProvider();
        var inbox = services.GetRequiredService(ReceiverTestServices.GetImplementationType("DurableInboxExtension"));
        var error = Assert.Throws<InvalidOperationException>(() => ActivatorUtilities.CreateInstance(provider,
            ReceiverTestServices.GetImplementationType("DurableMessagingJournalObserver"), inbox));
        Assert.Contains("DurableMessagingJournalEndpoint", error.Message, StringComparison.Ordinal);
    }

}
