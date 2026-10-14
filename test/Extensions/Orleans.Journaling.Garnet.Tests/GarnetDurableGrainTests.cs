using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Journaling.Tests;
using Orleans.TestingHost;
using StackExchange.Redis;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// A test cluster whose silos share one Garnet journal partition.
/// </summary>
public sealed class GarnetIntegrationTestFixture : IntegrationTestFixture
{
    // Field initializers run before the base constructor configures the cluster.
    private readonly (string Directory, GarnetJournalPartitionHost Host) _partition = StartPartition();

    public System.Net.IPEndPoint PartitionEndPoint => _partition.Host.EndPoint;

    protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
        var endPoint = _partition.Host.EndPoint;
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            // Replace the base fixture's volatile journal storage.
            siloBuilder.Services.RemoveAll<IJournalStorageProvider>();
            siloBuilder.AddGarnetJournalStorage(options =>
            {
                options.ConfigurationOptions = new ConfigurationOptions { EndPoints = { endPoint } };
                options.OperationTimeout = TimeSpan.FromSeconds(10);
            });
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _partition.Host.Dispose();
        GarnetPartitionFixture.TryDeleteDirectory(_partition.Directory);
    }

    private static (string, GarnetJournalPartitionHost) StartPartition()
    {
        var directory = GarnetPartitionFixture.CreateTemporaryDirectory();
        var host = GarnetJournalPartitionHost.Start(
            new GarnetJournalPartitionSettings { DataDirectory = directory, LogMemorySize = "64m", IndexMemorySize = "1m" },
            loggerFactory: null);
        return (directory, host);
    }
}

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
public sealed class GarnetDurableGrainTests(GarnetIntegrationTestFixture fixture)
    : DurableGrainTests(fixture), IClassFixture<GarnetIntegrationTestFixture>
{
    [Fact]
    public async Task GrainState_IsStoredInGarnetPartition()
    {
        var grain = fixture.Client.GetGrain<ITestDurableGrainInterface>(Guid.NewGuid());
        await grain.SetValues("garnet", 42);

        await using var connection = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { fixture.PartitionEndPoint },
            Protocol = RedisProtocol.Resp2,
        });
        var client = new GarnetJournalPartitionClient(connection.GetDatabase(), GarnetJournalStorageOptions.DEFAULT_BUCKET_COUNT, TimeSpan.FromSeconds(10));
        var storage = new GarnetJournalStorage(
            client,
            JournalId.FromGrainId(grain.GetGrainId()),
            "json",
            compactionThresholdBytes: 0);
        var consumer = new CapturingConsumer();
        await storage.ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.NotEmpty(consumer.Bytes);
    }
}
