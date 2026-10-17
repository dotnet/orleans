using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Messaging.Tests.Support;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using Orleans.Timers;
using Xunit;

namespace Orleans.Messaging.Tests.Functional;

[Collection(MessagingClusterCollection.Name)]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Messaging")]
public sealed class MessagingProviderCutoverTests
{
    private const string JournalFormatKey = "orleans-binary";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PublicHosting_NamedFactories_CommitsCompositeEndpointsAndRecoversFreshActivation()
    {
        var state = new ControlledJournalStorageProvider();
        var snapshots = new NamedFactoryMessagingProbe();
        var builder = new InProcessTestClusterBuilder(1);
        builder.ConfigureSilo((_, silo) =>
        {
            silo.AddVolatileJournalStorage();
            silo.AddJournalStorage("state", services =>
            {
                state.Configure(services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
                return state;
            });
            silo.AddVolatileJournalStorage("jobs");
            silo.UseJournaledDurableJobs(options => options.ActiveProviderName = "jobs");
            silo.Services.Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = JournalFormatKey);
            silo.AddMessaging();
            silo.Services.AddSingleton(snapshots);
            silo.Services.AddScoped<IJournaledStateManager>(services =>
            {
                var context = services.GetRequiredService<IGrainContext>();
                var manager = services.GetRequiredKeyedService<IJournaledStateManagerFactory>("state")
                    .CreateStandalone(JournalId.FromGrainId(context.GrainId));
                ((ILifecycleParticipant<IGrainLifecycle>)manager).Participate(context.ObservableLifecycle);
                return manager;
            });
        });
        await using var cluster = builder.Build();
        await cluster.DeployAsync(Token);
        var sender = cluster.Client.GetGrain<INamedFactoryMessagingGrain>(Guid.NewGuid());
        var receiver = cluster.Client.GetGrain<INamedFactoryMessagingGrain>(Guid.NewGuid());
        var sink = cluster.Client.GetGrain<INamedFactoryMessagingGrain>(Guid.NewGuid());
        var logicalId = MessageId.Create("cutover", Guid.NewGuid().ToString("N"));
        var receivedTask = snapshots.WaitAsync(receiver.GetGrainId(), Token);
        var forwardedTask = snapshots.WaitAsync(sink.GetGrainId(), Token);
        await sender.SendAsync(receiver.GetGrainId(), "messages/record", new TestMessage(logicalId, 1, "composite", sink.GetGrainId()));
        var received = await receivedTask;
        var forwarded = await forwardedTask;
        Assert.Equal(0, received.OutboxCount);
        Assert.Equal(0, forwarded.OutboxCount);
        Assert.Equal(new MessageEffect(logicalId, 1, 1, "composite"), Assert.Single(received.Effects));
        Assert.Equal(Assert.Single(received.Effects), Assert.Single(forwarded.Effects));
        Assert.True(cluster.TryGetGrainContext(receiver.GetGrainId(), out var oldContext));
        var oldManager = oldContext.ActivationServices.GetRequiredService<IJournaledStateManager>();
        Assert.False(oldManager is IDurableStateManager);
        await receiver.RequestDeactivationAsync();
        await oldContext.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), Token);
        var recovered = await receiver.GetSnapshotAsync();
        Assert.NotEqual(received.ActivationId, recovered.ActivationId);
        Assert.Equal(received.Effects, recovered.Effects);
        Assert.True(cluster.TryGetGrainContext(receiver.GetGrainId(), out var newContext));
        Assert.NotSame(oldManager, newContext.ActivationServices.GetRequiredService<IJournaledStateManager>());
        var journal = JournalId.FromGrainId(receiver.GetGrainId());
        Assert.NotNull(await state.CreateStorage(journal).GetMetadataAsync(Token));
        Assert.Null(await cluster.Silos[0].ServiceProvider.GetRequiredService<IJournalStorageProvider>().CreateStorage(journal).GetMetadataAsync(Token));
    }
}
