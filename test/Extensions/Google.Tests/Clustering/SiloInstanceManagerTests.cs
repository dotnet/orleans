using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TestExtensions;
using Orleans.Runtime;
using Orleans.Clustering.GoogleFirestore;

namespace Orleans.Tests.Google.Clustering;

[TestCategory("Functional"), TestCategory("GoogleFirestore"), TestCategory("GoogleCloud")]
public class SiloInstanceManagerTests : IClassFixture<GoogleCloudFixture>, IAsyncLifetime
{
    private readonly FirestoreOptions _options;
    private readonly string _clusterId;
    private OrleansSiloInstanceManager _manager = default!;
    private SiloInstanceEntity _entity = default!;
    private int _generation;
    private SiloAddress _siloAddress;

    public SiloInstanceManagerTests(GoogleCloudFixture fixture)
    {
        var id = $"Orleans-Test-{Guid.NewGuid()}";
        this._options = new FirestoreOptions
        {
            ProjectId = "orleans-test",
            EmulatorHost = fixture.Emulator.FirestoreEndpoint,
            RootCollectionName = id
        };

        this._clusterId = "test-" + Guid.NewGuid();
        this._generation = SiloAddress.AllocateNewGeneration();
        this._siloAddress = SiloAddressUtils.NewLocalSiloAddress(this._generation);
    }

    [Fact]
    public async Task ActivateSiloInstance()
    {
        await RegisterSiloInstance();

        await this._manager.ActivateSiloInstance(this._entity);
    }

    [Fact]
    public async Task UnregisterSiloInstance()
    {
        await RegisterSiloInstance();

        await this._manager.UnregisterSiloInstance(this._entity);
    }

    [Fact]
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

        await Task.Delay(TimeSpan.FromSeconds(3));

        await this._manager.CleanupDefunctSiloEntries(DateTimeOffset.UtcNow - TimeSpan.FromMicroseconds(1));

        var entries = await this._manager.FindAllSiloEntries();
        Assert.Equal(4, entries.Length);
        Assert.All(entries, e => Assert.NotEqual(OrleansSiloInstanceManager.INSTANCE_STATUS_DEAD, e.Item1.Status));
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task FindAllGatewayProxyEndpoints()
    {
        await RegisterSiloInstance();

        var gateways = await this._manager.FindAllGatewayProxyEndpoints();
        Assert.Equal(0, gateways.Count);  // "Number of gateways before Silo.Activate"

        await this._manager.ActivateSiloInstance(this._entity);

        gateways = await this._manager.FindAllGatewayProxyEndpoints();
        Assert.Equal(1, gateways.Count);  // "Number of gateways after Silo.Activate"

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
            Port = myEndpoint.Port.ToString(CultureInfo.InvariantCulture),
            Generation = this._generation,
            HostName = myEndpoint.Address.ToString(),
            ProxyPort = 30000,
            SiloName = "MyInstance",
            StartTime = DateTimeOffset.UtcNow
        };

        await this._manager.RegisterSiloInstance(this._entity);
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
        // Assert.Equal(referenceEntry.StartTime, entry.StartTime);
        Assert.Equal(referenceEntry.IAmAliveTime, entry.IAmAliveTime);

        Assert.Equal(referenceEntry.SuspectingTimes, entry.SuspectingTimes);
        Assert.Equal(referenceEntry.SuspectingSilos, entry.SuspectingSilos);
    }

    public async Task InitializeAsync()
    {
        this._manager = await OrleansSiloInstanceManager.GetManager(
            this._clusterId,
            NullLoggerFactory.Instance,
            this._options);

        await this._manager.TryCreateTableVersionEntryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
}