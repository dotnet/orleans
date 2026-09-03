using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TestExtensions;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.Clustering.Firestore;

namespace Orleans.Clustering.Firestore.Tests;

[TestSuite("Functional")]
[TestProvider("GoogleCloud")]
[TestCategory("Firestore"), TestCategory("GoogleCloud"), TestCategory("Functional")]
public class FirestoreClusteringTests : IAsyncLifetime
{
    private const string ClusterGroup = "Cluster";
    private static readonly DateTimeOffset TestStartTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private string _clusterId = default!;
    private FirestoreMembershipTable _membershipTable = default!;
    private FirestoreGatewayListProvider _gatewayProvider = default!;
    private FirestoreDataManager _storage = default!;
    private SiloInstanceEntity _entity = default!;
    private int _generation = default!;
    private SiloAddress _siloAddress = default!;

    [Fact]
    public async Task CleanDeadSiloInstance()
    {
        this._generation = 0;
        await WriteSiloInstance(SiloStatus.Dead, TestContext.Current.CancellationToken);

        // Create new active entries
        for (int i = 1; i < 5; i++)
        {
            this._generation = i;
            this._siloAddress = SiloAddressUtils.NewLocalSiloAddress(this._generation);
            await WriteSiloInstance(SiloStatus.Active, TestContext.Current.CancellationToken);
        }

        await this._membershipTable.CleanupDefunctSiloEntries(TestStartTime.AddTicks(1));

        var membership = await this._membershipTable.ReadAll();
        Assert.Equal(4, membership.Members.Count);
        Assert.All(membership.Members, member => Assert.NotEqual(SiloStatus.Dead, member.Item1.Status));
    }

    [Fact]
    public async Task CleanDefunctSilosSupportsMoreThanOneWriteBatch()
    {
        const int count = FirestoreDataManager.MaxBatchSize + 1;
        var entries = Enumerable.Range(0, count).Select(i => new SiloInstanceEntity
        {
            Id = SiloAddressUtils.NewLocalSiloAddress(i + 1).ToParsableString(),
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
            await Task.WhenAll(chunk.Select(entity => this._storage.UpsertEntity(
                entity,
                TestContext.Current.CancellationToken)));
        }

        await this._membershipTable.CleanupDefunctSiloEntries(TestStartTime.AddTicks(1));

        var membership = await this._membershipTable.ReadAll();
        Assert.Empty(membership.Members);
    }

    [Fact]
    public async Task FindAllGatewayProxyEndpoints()
    {
        await WriteSiloInstance(SiloStatus.Created, TestContext.Current.CancellationToken);

        var gateways = await this._gatewayProvider.GetGateways();
        Assert.Empty(gateways);  // "Number of gateways before Silo.Activate"

        this._entity.Status = (int)SiloStatus.Active;
        await this._storage.UpsertEntity(this._entity, TestContext.Current.CancellationToken);

        gateways = await this._gatewayProvider.GetGateways();
        Assert.Single(gateways);  // "Number of gateways after Silo.Activate"

        Uri myGateway = gateways.First();
        Assert.Equal(this._entity.Address, myGateway.Host.ToString());  // "Gateway address"
        Assert.Equal(this._entity.ProxyPort, myGateway.Port);  // "Gateway port"
    }

    [Fact]
    public async Task UpdateIAmAliveDoesNotOverwriteNewerHeartbeat()
    {
        var current = TestStartTime.AddMinutes(2);
        await WriteSiloInstance(SiloStatus.Active, TestContext.Current.CancellationToken, current);

        var entry = this._entity.ToMembershipEntry();
        entry.IAmAliveTime = current.AddMinutes(-1).UtcDateTime;
        await this._membershipTable.UpdateIAmAlive(entry);

        var row = await this._membershipTable.ReadRow(entry.SiloAddress);
        Assert.Equal(current.UtcDateTime, Assert.Single(row.Members).Item1.IAmAliveTime);

        entry.IAmAliveTime = current.AddMinutes(1).UtcDateTime;
        await this._membershipTable.UpdateIAmAlive(entry);

        row = await this._membershipTable.ReadRow(entry.SiloAddress);
        Assert.Equal(entry.IAmAliveTime, Assert.Single(row.Members).Item1.IAmAliveTime);
    }

    [Fact]
    public async Task ReadRowReturnsVersionWhenSiloDoesNotExist()
    {
        var expected = await this._membershipTable.ReadAll();

        var actual = await this._membershipTable.ReadRow(SiloAddressUtils.NewLocalSiloAddress(this._generation + 1));

        Assert.Empty(actual.Members);
        Assert.Equal(expected.Version.Version, actual.Version.Version);
        Assert.Equal(expected.Version.VersionEtag, actual.Version.VersionEtag);
    }

    [Fact]
    public async Task UpdateRowRefreshesEtagForIdenticalMembership()
    {
        var entry = new MembershipEntry
        {
            SiloAddress = this._siloAddress,
            HostName = "localhost",
            SiloName = "TestSilo",
            Status = SiloStatus.Active,
            StartTime = TestStartTime.UtcDateTime,
            IAmAliveTime = TestStartTime.UtcDateTime,
        };
        var table = await this._membershipTable.ReadAll();
        Assert.True(await this._membershipTable.InsertRow(entry, table.Version.Next()));
        var inserted = await this._membershipTable.ReadRow(entry.SiloAddress);
        var insertedEtag = Assert.Single(inserted.Members).Item2;

        Assert.True(await this._membershipTable.UpdateRow(entry, insertedEtag, inserted.Version.Next()));

        var updated = await this._membershipTable.ReadRow(entry.SiloAddress);
        Assert.NotEqual(insertedEtag, Assert.Single(updated.Members).Item2);
    }

    private async Task WriteSiloInstance(
        SiloStatus status,
        CancellationToken cancellationToken,
        DateTimeOffset? iAmAliveTime = null)
    {
        IPEndPoint myEndpoint = this._siloAddress.Endpoint;

        this._entity = new SiloInstanceEntity
        {
            Id = this._siloAddress.ToParsableString(),
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
            StartTime = TestStartTime,
            IAmAliveTime = iAmAliveTime ?? TestStartTime,
        };

        var etag = await this._storage.UpsertEntity(this._entity, cancellationToken);
        this._entity.ETag = Clustering.Firestore.Utils.ParseTimestamp(etag);
    }

    public async ValueTask InitializeAsync()
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

        var clusterOptions = Options.Create(new ClusterOptions { ClusterId = this._clusterId });
        var firestoreOptions = Options.Create(options);
        this._membershipTable = new FirestoreMembershipTable(
            NullLoggerFactory.Instance,
            firestoreOptions,
            clusterOptions);
        await this._membershipTable.InitializeMembershipTable(tryInitTableVersion: true);

        this._gatewayProvider = new FirestoreGatewayListProvider(
            NullLoggerFactory.Instance,
            firestoreOptions,
            clusterOptions,
            Options.Create(new GatewayOptions()));
        await this._gatewayProvider.InitializeGatewayListProvider();

        this._storage = new FirestoreDataManager(
            ClusterGroup,
            Utils.SanitizeId(this._clusterId),
            options,
            NullLogger<FirestoreDataManager>.Instance);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
