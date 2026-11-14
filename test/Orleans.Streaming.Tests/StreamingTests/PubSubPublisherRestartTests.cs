using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.Streams;
using Orleans.TestingHost;
using UnitTests.StorageTests;
using Xunit;

namespace UnitTests.StreamingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class PubSubPublisherRestartTests
{
    [Fact, TestCategory("BVT"), TestCategory("Streaming"), TestCategory("PubSub")]
    public async Task RegisterProducer_RecreatedMembershipAtSameVersionRemovesPersistedPublisher()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var storageServices = new ServiceCollection().AddLogging().AddSerializer().BuildServiceProvider();
        var storage = new MockStorageProvider(
            "PubSubStore",
            storageServices.GetRequiredService<ILoggerFactory>(),
            storageServices.GetRequiredService<DeepCopier>());
        var serviceId = Guid.NewGuid().ToString("N");
        var streamId = new QualifiedStreamId("ProviderName", StreamId.Create("StreamNamespace", Guid.NewGuid()));
        await using var originalCluster = CreateCluster();
        await originalCluster.DeployAsync(cancellationToken);
        var originalSilo = Assert.Single(originalCluster.Silos);
        var originalSnapshot = originalSilo.ServiceProvider.GetRequiredService<IClusterMembershipService>().CurrentSnapshot;
        var originalProducer = SystemTargetGrainId.Create(
            Constants.StreamPullingAgentType,
            originalSilo.SiloAddress,
            "ProviderName_1_test-queue").GrainId;
        var rendezvous = originalCluster.Client.GetGrain<IPubSubRendezvousGrain>(streamId.ToString());

        await rendezvous.RegisterProducer(streamId, originalProducer, originalSnapshot.Version, cancellationToken);
        var persistedPublisher = Assert.Single(Assert.IsType<PubSubGrainState>(storage.GetLastState()).Producers);
        Assert.Equal(originalProducer, persistedPublisher.Producer);
        Assert.Equal(originalSnapshot.Version, persistedPublisher.MembershipVersion);

        // Allocate the replacement cluster while the original silo still owns its endpoint.
        await using var replacementCluster = CreateCluster();
        await originalCluster.DisposeAsync();

        await replacementCluster.DeployAsync(cancellationToken);
        var replacementSilo = Assert.Single(replacementCluster.Silos);
        var replacementSnapshot = replacementSilo.ServiceProvider.GetRequiredService<IClusterMembershipService>().CurrentSnapshot;
        Assert.NotEqual(originalSilo.SiloAddress.Endpoint, replacementSilo.SiloAddress.Endpoint);
        Assert.Equal(originalSnapshot.Version, replacementSnapshot.Version);
        Assert.Equal(SiloStatus.None, replacementSnapshot.GetSiloStatus(originalSilo.SiloAddress));
        Assert.Equal(SiloStatus.Active, replacementSnapshot.GetSiloStatus(replacementSilo.SiloAddress));
        var replacementProducer = SystemTargetGrainId.Create(
            Constants.StreamPullingAgentType,
            replacementSilo.SiloAddress,
            "ProviderName_1_test-queue").GrainId;
        rendezvous = replacementCluster.Client.GetGrain<IPubSubRendezvousGrain>(streamId.ToString());
        Assert.Equal(1, await rendezvous.ProducerCount(streamId, cancellationToken));

        await rendezvous.RegisterProducer(streamId, replacementProducer, replacementSnapshot.Version, cancellationToken);

        var replacementPublisher = Assert.Single(Assert.IsType<PubSubGrainState>(storage.GetLastState()).Producers);
        Assert.Equal(replacementProducer, replacementPublisher.Producer);
        Assert.Equal(replacementSnapshot.Version, replacementPublisher.MembershipVersion);
        await replacementCluster.Client.GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero, cancellationToken);
        Assert.Equal(1, await rendezvous.ProducerCount(streamId, cancellationToken));
        await rendezvous.UnregisterProducer(streamId, replacementProducer, cancellationToken);
        Assert.Equal(0, await rendezvous.ProducerCount(streamId, cancellationToken));

        InProcessTestCluster CreateCluster()
        {
            var builder = new InProcessTestClusterBuilder(1);
            builder.Options.ClusterId = serviceId;
            builder.Options.ServiceId = serviceId;
            builder.ConfigureSilo((_, siloBuilder) =>
            {
                siloBuilder.Services.AddSiloStreaming();
                siloBuilder.Services.AddKeyedSingleton<IGrainStorage>("PubSubStore", storage);
            });
            return builder.Build();
        }
    }
}
