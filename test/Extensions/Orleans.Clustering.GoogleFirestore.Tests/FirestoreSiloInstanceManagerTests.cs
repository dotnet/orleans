using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TestExtensions;
using Orleans.Runtime;
using Orleans.Clustering.GoogleFirestore;

namespace Orleans.Clustering.GoogleFirestore.Tests;

[TestCategory("GoogleFirestore"), TestCategory("GoogleCloud"), TestCategory("Functional")]
public class FirestoreSiloInstanceManagerTests : IAsyncLifetime
{
    private static readonly DateTimeOffset TestStartTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private FirestoreOptions _options = default!;
    private string _clusterId = default!;
    private OrleansSiloInstanceManager _manager = default!;
    private SiloInstanceEntity _entity = default!;
    private int _generation = default!;
    private SiloAddress _siloAddress = default!;

    [SkippableFact]
    public async Task CleanDeadSiloInstance()
    {
        this._generation = 0;
        await RegisterSiloInstance();
        // and mark it as dead
        await this._manager.UnregisterSiloInstance(this._entity);

        // Create new active entries
        for (int i = 1; i < 5; i++)
        {
            this._generation = i;
            this._siloAddress = SiloAddressUtils.NewLocalSiloAddress(this._generation);
            var instance = await RegisterSiloInstance();
            await this._manager.ActivateSiloInstance(instance);
        }

        await this._manager.CleanupDefunctSiloEntries(TestStartTime.AddTicks(1));

        var mbrData = await this._manager.FindAllSiloEntries();
        Assert.Equal(4, mbrData.Silos.Length);
        Assert.All(mbrData.Silos, e => Assert.NotEqual(OrleansSiloInstanceManager.INSTANCE_STATUS_DEAD, e.Status));
    }

    [SkippableFact]
    public async Task Register_CheckData()
    {
        await RegisterSiloInstance();

        await this._manager.RegisterSiloInstance(this._entity);

        var (silo, version) = await this._manager.FindSiloAndVersionEntities(this._siloAddress);

        Assert.NotNull(version); // ETag should not be null
        Assert.NotNull(silo); // Silo instance should not be null

        Assert.Equal(OrleansSiloInstanceManager.INSTANCE_STATUS_CREATED, silo.Status);

        CheckSiloInstanceTableEntry(this._entity, silo);
    }

    [SkippableFact]
    public async Task Activate_CheckData()
    {
        await RegisterSiloInstance();

        await this._manager.ActivateSiloInstance(this._entity);

        var (silo, version) = await this._manager.FindSiloAndVersionEntities(this._siloAddress);

        Assert.NotNull(version); // ETag should not be null
        Assert.NotNull(silo); // Silo instance should not be null

        Assert.Equal(OrleansSiloInstanceManager.INSTANCE_STATUS_ACTIVE, silo.Status);

        CheckSiloInstanceTableEntry(this._entity, silo);
    }

    [SkippableFact]
    public async Task Unregister_CheckData()
    {
        await RegisterSiloInstance();

        await this._manager.UnregisterSiloInstance(this._entity);

        var (silo, version) = await this._manager.FindSiloAndVersionEntities(this._siloAddress);

        Assert.NotNull(version); // ETag should not be null
        Assert.NotNull(silo); // Silo instance should not be null

        Assert.Equal(OrleansSiloInstanceManager.INSTANCE_STATUS_DEAD, silo.Status);

        CheckSiloInstanceTableEntry(this._entity, silo);
    }

    [SkippableFact]
    public async Task FindAllGatewayProxyEndpoints()
    {
        await RegisterSiloInstance();

        var gateways = await this._manager.FindAllGatewayProxyEndpoints();
        Assert.Empty(gateways);  // "Number of gateways before Silo.Activate"

        await this._manager.ActivateSiloInstance(this._entity);

        gateways = await this._manager.FindAllGatewayProxyEndpoints();
        Assert.Single(gateways);  // "Number of gateways after Silo.Activate"

        Uri myGateway = gateways.First();
        Assert.Equal(this._entity.Address, myGateway.Host.ToString());  // "Gateway address"
        Assert.Equal(this._entity.ProxyPort, myGateway.Port);  // "Gateway port"
    }

    private async Task<SiloInstanceEntity> RegisterSiloInstance()
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
            UpdateZone = 3,
            FaultZone = 5,
            StartTime = TestStartTime
        };

        var etag = await this._manager.RegisterSiloInstance(this._entity);
        this._entity.ETag = Clustering.GoogleFirestore.Utils.ParseTimestamp(etag);

        return this._entity;
    }

    private static void CheckSiloInstanceTableEntry(SiloInstanceEntity referenceEntry, SiloInstanceEntity entry)
    {
        Assert.Equal(referenceEntry.Id, entry.Id);
        Assert.Equal(referenceEntry.ClusterId, entry.ClusterId);
        Assert.Equal(referenceEntry.Address, entry.Address);
        Assert.Equal(referenceEntry.Port, entry.Port);
        Assert.Equal(referenceEntry.Generation, entry.Generation);
        Assert.Equal(referenceEntry.HostName, entry.HostName);
        Assert.Equal(referenceEntry.ProxyPort, entry.ProxyPort);
        Assert.Equal(referenceEntry.SiloName, entry.SiloName);
        Assert.Equal(referenceEntry.RoleName, entry.RoleName);
        Assert.Equal(referenceEntry.UpdateZone, entry.UpdateZone);
        Assert.Equal(referenceEntry.FaultZone, entry.FaultZone);
        Assert.Equal(referenceEntry.IAmAliveTime, entry.IAmAliveTime);

        Assert.Equal(referenceEntry.SuspectingSilos, entry.SuspectingSilos);
    }

    public async Task InitializeAsync()
    {
        var id = $"orleans-test-{Guid.NewGuid():N}";
        this._options = new FirestoreOptions
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
            this._options);

        await this._manager.TryCreateTableVersionEntryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}