using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TestExtensions;
using Orleans.Runtime;
using Orleans.Clustering.GoogleFirestore;

namespace Orleans.Clustering.GoogleFirestore.Tests;

[TestCategory("GoogleFirestore"), TestCategory("GoogleCloud"), TestCategory("Functional")]
public class FirestoreSiloInstanceManagerTests : IAsyncLifetime
{
    private const string ClusterGroup = "Cluster";
    private static readonly DateTimeOffset TestStartTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private string _clusterId = default!;
    private OrleansSiloInstanceManager _manager = default!;
    private FirestoreDataManager _storage = default!;
    private SiloInstanceEntity _entity = default!;
    private int _generation = default!;
    private SiloAddress _siloAddress = default!;

    [SkippableFact]
    public async Task CleanDeadSiloInstance()
    {
        this._generation = 0;
        await WriteSiloInstance(SiloStatus.Dead);

        // Create new active entries
        for (int i = 1; i < 5; i++)
        {
            this._generation = i;
            this._siloAddress = SiloAddressUtils.NewLocalSiloAddress(this._generation);
            await WriteSiloInstance(SiloStatus.Active);
        }

        await this._manager.CleanupDefunctSiloEntries(TestStartTime.AddTicks(1));

        var mbrData = await this._manager.FindAllSiloEntries();
        Assert.Equal(4, mbrData.Silos.Length);
        Assert.All(mbrData.Silos, e => Assert.NotEqual((int)SiloStatus.Dead, e.Status));
    }

    [SkippableFact]
    public async Task CleanDefunctSilosSupportsMoreThanOneWriteBatch()
    {
        const int count = FirestoreDataManager.MAX_BATCH_ENTRIES + 1;
        var entries = Enumerable.Range(0, count).Select(i => new SiloInstanceEntity
        {
            Id = SiloAddressUtils.NewLocalSiloAddress(i + 1).ToParsableString(),
            ClusterId = this._clusterId,
            Address = IPAddress.Loopback.ToString(),
            Port = 10000 + i,
            Generation = i + 1,
            HostName = IPAddress.Loopback.ToString(),
            ProxyPort = 30000 + i,
            SiloName = $"Silo-{i}",
            RoleName = "Test",
            Status = (int)SiloStatus.Dead,
            StartTime = TestStartTime,
        }).ToArray();

        foreach (var chunk in entries.Chunk(50))
        {
            await Task.WhenAll(chunk.Select(this._storage.UpsertEntity));
        }

        await this._manager.CleanupDefunctSiloEntries(TestStartTime.AddTicks(1));

        var membership = await this._manager.FindAllSiloEntries();
        Assert.Empty(membership.Silos);
    }

    [SkippableFact]
    public async Task FindAllGatewayProxyEndpoints()
    {
        await WriteSiloInstance(SiloStatus.Created);

        var gateways = await this._manager.FindAllGatewayProxyEndpoints();
        Assert.Empty(gateways);  // "Number of gateways before Silo.Activate"

        this._entity.Status = (int)SiloStatus.Active;
        await this._storage.UpsertEntity(this._entity);

        gateways = await this._manager.FindAllGatewayProxyEndpoints();
        Assert.Single(gateways);  // "Number of gateways after Silo.Activate"

        Uri myGateway = gateways.First();
        Assert.Equal(this._entity.Address, myGateway.Host.ToString());  // "Gateway address"
        Assert.Equal(this._entity.ProxyPort, myGateway.Port);  // "Gateway port"
    }

    private async Task WriteSiloInstance(SiloStatus status)
    {
        IPEndPoint myEndpoint = this._siloAddress.Endpoint;

        this._entity = new SiloInstanceEntity
        {
            Id = this._siloAddress.ToParsableString(),
            ClusterId = this._clusterId,
            Address = myEndpoint.Address.ToString(),
            Port = myEndpoint.Port,
            Generation = this._generation,
            HostName = myEndpoint.Address.ToString(),
            ProxyPort = 30000,
            SiloName = "MyInstance",
            RoleName = "MyRole",
            Status = (int)status,
            UpdateZone = 3,
            FaultZone = 5,
            StartTime = TestStartTime
        };

        var etag = await this._storage.UpsertEntity(this._entity);
        this._entity.ETag = Clustering.GoogleFirestore.Utils.ParseTimestamp(etag);
    }

    public async Task InitializeAsync()
    {
        var id = $"orleans-test-{Guid.NewGuid():N}";
        var options = new FirestoreOptions
        {
            ProjectId = GoogleEmulatorHost.ProjectId,
            EmulatorHost = GoogleEmulatorHost.FirestoreEndpoint,
            RootCollectionName = id
        };

        this._clusterId = id;
        this._generation = SiloAddress.AllocateNewGeneration();
        this._siloAddress = SiloAddressUtils.NewLocalSiloAddress(this._generation);

        this._manager = await OrleansSiloInstanceManager.GetManager(
            this._clusterId,
            NullLoggerFactory.Instance,
            options);

        this._storage = new FirestoreDataManager(
            ClusterGroup,
            this._clusterId,
            options,
            NullLogger<FirestoreDataManager>.Instance);

        await this._manager.TryCreateTableVersionEntryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}