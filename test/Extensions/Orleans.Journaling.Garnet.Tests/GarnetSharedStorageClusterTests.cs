using System.Diagnostics;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration;
using Orleans.Core.Internal;
using Orleans.Journaling.Ranges;
using Orleans.Journaling.Tests;
using Orleans.TestingHost;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// A three-silo cluster whose three partitions are on shared blob storage. The initial map names no silo as a host, so
/// the silos' failover agents place the partitions; membership detects dead silos quickly.
/// </summary>
internal class GarnetSharedStorageClusterFixture : IntegrationTestFixture
{
    internal const int BucketCount = 64;

    // Field and property initializers run before the base constructor configures the cluster.
    public BlobContainerClient Container { get; } = GarnetBlobTestStorage.CreateContainer();

    public InMemoryRangeMetaStore MetaStore { get; } = new();

    /// <summary>Gets how often silos balance partitions across silos.</summary>
    protected virtual TimeSpan BalancingInterval => Timeout.InfiniteTimeSpan;

    protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 3;
        var metaStore = MetaStore;
        var container = Container;
        var balancingInterval = BalancingInterval;
        var map = RangeMap.CreateUniform(
            BucketCount,
            Enumerable.Range(0, 3).Select(index => KeyValuePair.Create(new PartitionId($"p{index}"), new PartitionHosting("unassigned", 0, $"p{index}"))));
        builder.ConfigureSilo((options, siloBuilder) =>
        {
            siloBuilder.Services.RemoveAll<IJournalStorageProvider>();
            siloBuilder.Configure<ClusterMembershipOptions>(membership =>
            {
                membership.ProbeTimeout = TimeSpan.FromSeconds(1);
                membership.NumMissedProbesLimit = 2;
                membership.NumVotesForDeathDeclaration = 1;
            });
            siloBuilder.AddGarnetJournalStorage(storage =>
            {
                storage.MetaStore = metaStore;
                storage.PartitionBlobContainer = container;
                storage.InitialRangeMap = map;
                storage.LogMemorySize = "64m";
                storage.IndexMemorySize = "1m";
                storage.OperationTimeout = TimeSpan.FromSeconds(30);
                storage.HostingInterval = TimeSpan.FromMilliseconds(200);
                storage.MigrationInterval = TimeSpan.FromMilliseconds(50);
                storage.BalancingInterval = balancingInterval;
            });
        });
    }

    public sealed override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Container.DeleteIfExistsAsync();
    }
}

/// <summary>The shared-storage cluster, with silos which balance partitions across themselves every two seconds.</summary>
internal sealed class GarnetBalancedSharedStorageClusterFixture : GarnetSharedStorageClusterFixture
{
    protected override TimeSpan BalancingInterval => TimeSpan.FromSeconds(2);
}

/// <summary>
/// Kills a silo which hosts a partition on shared storage; membership declares it dead, another silo takes the
/// partition over, and grains recover their state.
/// </summary>
[TestSuite("Functional")]
[TestProvider("AzureStorage")]
[TestCategory("AzureStorage"), TestCategory("Functional")]
public sealed class GarnetSharedStorageClusterTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task KilledSilo_PartitionsFailOverAndGrainsRecoverTheirState()
    {
        GarnetBlobTestStorage.SkipIfUnavailable();
        await using var fixture = new GarnetSharedStorageClusterFixture();
        await fixture.InitializeAsync();
        var cluster = fixture.Cluster;

        // The failover agents place every partition on a live silo, which publishes its endpoint.
        var view = await GarnetTestWaits.WaitForMapAsync(fixture.MetaStore, map => map.Partitions.Values.All(hosting => hosting.Endpoint is not null && SiloAddress.TryParse(hosting.Host, out _)), "every partition is served by a silo", TimeSpan.FromSeconds(90));
        Task<string> DescribeAsync() => fixture.MetaStore.ReadAsync(CancellationToken.None).AsTask().ContinueWith(read => GarnetTestWaits.DescribeMap(read.Result), TaskScheduler.Default);
        var grains = Enumerable.Range(0, 32).Select(_ => fixture.Client.GetGrain<ITestDurableGrainInterface>(Guid.NewGuid())).ToArray();
        await GarnetTestWaits.BoundedAsync(Task.WhenAll(grains.Select((grain, index) => grain.SetValues("before", index))), "Grains writing before the kill", DescribeAsync);

        // Kill a silo which hosts a partition. The first silo to become active may host every partition; balancing
        // partitions across silos is the balancer's job.
        var hosting = cluster.Silos.Where(silo => view.Map.Partitions.Values.Any(hosting => hosting.Host == silo.SiloAddress.ToParsableString())).ToList();
        var victim = hosting.LastOrDefault(silo => silo != cluster.Silos[0]) ?? hosting[0];
        var victimHost = victim.SiloAddress.ToParsableString();
        var lost = view.Map.Partitions.Where(entry => entry.Value.Host == victimHost).Select(entry => entry.Key).ToList();
        var stopwatch = Stopwatch.StartNew();
        await GarnetTestWaits.BoundedAsync(cluster.KillSiloAsync(victim, CancellationToken), $"Killing silo {victimHost}", DescribeAsync);

        var failedOver = await GarnetTestWaits.WaitForMapAsync(
            fixture.MetaStore,
            map => lost.All(partition => map.Partitions[partition] is { Endpoint: not null } hosting && hosting.Host != victimHost && hosting.Epoch > view.Map.Partitions[partition].Epoch),
            $"partitions {string.Join(", ", lost)} of the killed silo are served by other silos",
            TimeSpan.FromSeconds(120));
        TestContext.Current.TestOutputHelper?.WriteLine($"Partitions {string.Join(", ", lost)} failed over in {stopwatch.Elapsed.TotalSeconds:F1} s.");

        // Grains read their state from the new hosts: GetValues returns an activation's memory, so deactivate every
        // grain first, wherever its activation survived, to make each one recover its journal.
        foreach (var grain in grains)
        {
            await GarnetTestWaits.BoundedAsync(grain.Cast<IGrainManagementExtension>().DeactivateOnIdle(CancellationToken).AsTask(), "Deactivating a grain after the failover", DescribeAsync);
        }

        for (var i = 0; i < grains.Length; i++)
        {
            Assert.Equal(("before", i), await GarnetTestWaits.BoundedAsync(grains[i].GetValues(), $"Grain {i} reading after the failover", DescribeAsync));
        }

        await GarnetTestWaits.BoundedAsync(Task.WhenAll(grains.Select((grain, index) => grain.SetValues("after", index))), "Grains writing after the failover", DescribeAsync);
        foreach (var grain in grains)
        {
            await GarnetTestWaits.BoundedAsync(grain.Cast<IGrainManagementExtension>().DeactivateOnIdle(CancellationToken).AsTask(), "Deactivating a grain", DescribeAsync);
        }

        for (var i = 0; i < grains.Length; i++)
        {
            Assert.Equal(("after", i), await GarnetTestWaits.BoundedAsync(grains[i].GetValues(), $"Grain {i} reading after reactivation", DescribeAsync));
        }

        Assert.All(lost, partition => Assert.NotEqual(victimHost, failedOver.Map.Partitions[partition].Host));
    }
    [Fact]
    public async Task Placement_SpreadsPartitionsAndActivatesGrainsWithTheirPartitions()
    {
        GarnetBlobTestStorage.SkipIfUnavailable();
        await using var fixture = new GarnetBalancedSharedStorageClusterFixture();
        await fixture.InitializeAsync();
        var silos = fixture.Cluster.Silos.Select(silo => silo.SiloAddress.ToParsableString()).ToHashSet();

        // The first silo to become active may receive every partition; the silos then move one at a time until each hosts one.
        var view = await GarnetTestWaits.WaitForMapAsync(
            fixture.MetaStore,
            map => map.Partitions.Values.All(hosting => hosting.Endpoint is not null) && map.Partitions.Values.Select(hosting => hosting.Host).ToHashSet().SetEquals(silos),
            "each silo serves one partition",
            TimeSpan.FromSeconds(90));

        // Grains with GarnetJournalPlacement activate on the silo hosting their journal's partition.
        var grains = Enumerable.Range(0, 24).Select(_ => fixture.Client.GetGrain<IGarnetLocalityGrain>(Guid.NewGuid())).ToArray();
        Task<string> Describe() => fixture.MetaStore.ReadAsync(CancellationToken.None).AsTask().ContinueWith(read => GarnetTestWaits.DescribeMap(read.Result), TaskScheduler.Default);
        await GarnetTestWaits.BoundedAsync(Task.WhenAll(grains.Select((grain, index) => grain.SetValue(index))), "Writing grain state", Describe);
        foreach (var grain in grains)
        {
            var journal = JournalId.FromGrainId(grain.GetGrainId());
            var bucket = GarnetJournalKeys.GetBucket(GarnetJournalKeys.EncodeJournalId(journal.Value), GarnetSharedStorageClusterFixture.BucketCount);
            Assert.Equal(view.Map.Partitions[view.Map.GetOwner(bucket)].Host, await GarnetTestWaits.BoundedAsync(grain.GetSilo(), "Reading a grain's silo", Describe));
        }

        for (var i = 0; i < grains.Length; i++)
        {
            Assert.Equal(i, await GarnetTestWaits.BoundedAsync(grains[i].GetValue(), "Reading grain state", Describe));
        }
    }
}
