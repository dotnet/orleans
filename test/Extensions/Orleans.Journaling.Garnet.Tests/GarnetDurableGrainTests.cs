using Orleans.Core.Internal;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Journaling.Ranges;
using Orleans.Journaling.Tests;
using Orleans.TestingHost;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// A three-silo test cluster whose journals are spread over four partitions: two silos host two partitions each, and
/// the third hosts none and routes every operation to the others.
/// </summary>
public sealed class GarnetIntegrationTestFixture : IntegrationTestFixture
{
    internal const int BucketCount = 64;

    // Field initializers run before the base constructor configures the cluster.
    private readonly string _directory = GarnetPartitionFixture.CreateTemporaryDirectory();

    public InMemoryRangeMetaStore MetaStore { get; } = new();

    /// <summary>Gets the static range map: four partitions, hosted by the first two silos.</summary>
    public static RangeMap InitialRangeMap { get; } = RangeMap.CreateUniform(
        BucketCount,
        [
            new(new PartitionId("p0"), new PartitionHosting("Silo_0", 0, "p0")),
            new(new PartitionId("p1"), new PartitionHosting("Silo_1", 0, "p1")),
            new(new PartitionId("p2"), new PartitionHosting("Silo_0", 0, "p2")),
            new(new PartitionId("p3"), new PartitionHosting("Silo_1", 0, "p3")),
        ]);

    protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 3;
        var metaStore = MetaStore;
        var directory = _directory;
        builder.ConfigureSilo((options, siloBuilder) =>
        {
            // Replace the base fixture's volatile journal storage.
            siloBuilder.Services.RemoveAll<IJournalStorageProvider>();
            siloBuilder.AddGarnetJournalStorage(storage =>
            {
                storage.MetaStore = metaStore;
                storage.InitialRangeMap = InitialRangeMap;
                storage.HostId = options.SiloName;
                storage.DataDirectory = options.SiloName is "Silo_2" ? null : Path.Combine(directory, options.SiloName);
                storage.LogMemorySize = "64m";
                storage.IndexMemorySize = "1m";
                storage.OperationTimeout = TimeSpan.FromSeconds(10);
                storage.MigrationInterval = TimeSpan.FromMilliseconds(50);
            });
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        GarnetPartitionFixture.TryDeleteDirectory(_directory);
    }
}

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
public sealed class GarnetDurableGrainTests(GarnetIntegrationTestFixture fixture)
    : DurableGrainTests(fixture), IClassFixture<GarnetIntegrationTestFixture>
{
    [Fact]
    public async Task GrainJournals_AreSpreadAcrossPartitionsAndListedByCatalog()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grains = Enumerable.Range(0, 32).Select(_ => fixture.Client.GetGrain<ITestDurableGrainInterface>(Guid.NewGuid())).ToArray();
        await Task.WhenAll(grains.Select((grain, index) => grain.SetValues("garnet", index)));

        // A provider which hosts no partitions observes the cluster's journals through the shared range map.
        var observer = GarnetPartitionFixture.CreateProvider(dataDirectory: null, fixture.MetaStore, options => options.HostId = "observer");
        await observer.StartAsync(cancellationToken);
        try
        {
            var view = observer.Router.View;
            Assert.Equal(4, view.Map.Partitions.Count);
            Assert.All(view.Map.Partitions.Values, hosting => Assert.NotNull(hosting.Endpoint));

            var owners = new HashSet<PartitionId>();
            foreach (var grain in grains)
            {
                var storage = (GarnetJournalStorage)observer.CreateStorage(JournalId.FromGrainId(grain.GetGrainId()));
                var consumer = new CapturingConsumer();
                await storage.ReadAsync(consumer, cancellationToken);
                Assert.NotEmpty(consumer.Bytes);
                owners.Add(view.Map.GetOwner(storage.Bucket));
            }

            // Both hosting silos serve journals.
            Assert.Contains(owners, owner => view.Map.Partitions[owner].Host == "Silo_0");
            Assert.Contains(owners, owner => view.Map.Partitions[owner].Host == "Silo_1");

            var listed = new HashSet<JournalId>();
            await foreach (var entry in observer.ListAsync(cancellationToken: cancellationToken))
            {
                listed.Add(entry.Id);
            }

            Assert.Superset(grains.Select(grain => JournalId.FromGrainId(grain.GetGrainId())).ToHashSet(), listed);
        }
        finally
        {
            await observer.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Migrations_DrivenByHostingSilos_PreserveGrainState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var grains = Enumerable.Range(0, 32).Select(_ => fixture.Client.GetGrain<ITestDurableGrainInterface>(Guid.NewGuid())).ToArray();
        await Task.WhenAll(grains.Select((grain, index) => grain.SetValues("before", index)));

        // A process which hosts no partitions commits the moves; the silos hosting the destinations drive them.
        var observer = GarnetPartitionFixture.CreateProvider(dataDirectory: null, fixture.MetaStore, options => options.HostId = "mover");
        await observer.StartAsync(cancellationToken);
        try
        {
            var map = observer.Router.View.Map;
            var p0 = Enumerable.Range(0, map.BucketCount).Where(bucket => map.GetOwner(bucket) == new PartitionId("p0")).ToArray();
            var p1 = Enumerable.Range(0, map.BucketCount).Where(bucket => map.GetOwner(bucket) == new PartitionId("p1")).ToArray();
            Assert.Equal(MoveStatus.Committed, (await observer.Coordinator.TryMoveAsync(p0[..(p0.Length / 2)], new PartitionId("p1"), cancellationToken)).Status);
            Assert.Equal(MoveStatus.Committed, (await observer.Coordinator.TryMoveAsync(p1, new PartitionId("p3"), cancellationToken)).Status);

            // Grains keep writing while their buckets move.
            await Task.WhenAll(grains.Select((grain, index) => grain.SetValues("during", index)));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while ((await fixture.MetaStore.ReadAsync(timeout.Token))!.Map.Migrations.Length > 0)
            {
                await Task.Delay(50, timeout.Token);
            }

            await Task.WhenAll(grains.Select((grain, index) => grain.SetValues("after", index)));
            map = (await fixture.MetaStore.ReadAsync(cancellationToken))!.Map;
            Assert.All(p1, bucket => Assert.Equal(new PartitionId("p3"), map.GetOwner(bucket)));
        }
        finally
        {
            await observer.StopAsync(cancellationToken);
        }

        // New activations recover each grain's state from its journal at the bucket's new owner.
        foreach (var grain in grains)
        {
            await grain.Cast<IGrainManagementExtension>().DeactivateOnIdle(cancellationToken);
        }

        for (var i = 0; i < grains.Length; i++)
        {
            Assert.Equal(("after", i), await grains[i].GetValues());
        }
    }
}
