using System.Net;
using Azure.Storage.Blobs;
using Orleans.Journaling.Ranges;
using TestExtensions;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Azure Storage containers for tests of partitions on shared blob storage, and partitions hosted on them.
/// </summary>
internal static class GarnetBlobTestStorage
{
    public static void SkipIfUnavailable()
    {
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            Assert.SkipWhen(string.IsNullOrEmpty(TestDefaultConfiguration.DataBlobUri?.ToString()), "DataBlobUri is not set. Skipping test.");
        }
        else
        {
            Assert.SkipWhen(string.IsNullOrEmpty(TestDefaultConfiguration.DataConnectionString), "DataConnectionString is not set. Skipping test.");
        }
    }

    public static BlobContainerClient CreateContainer()
    {
        var service = TestDefaultConfiguration.UseAadAuthentication
            ? new BlobServiceClient(TestDefaultConfiguration.DataBlobUri, TestDefaultConfiguration.TokenCredential)
            : new BlobServiceClient(TestDefaultConfiguration.DataConnectionString);
        return service.GetBlobContainerClient($"garnet-partitions-{Guid.NewGuid():N}");
    }

    /// <summary>Takes a partition's storage over at <paramref name="epoch"/> and starts a partition on it.</summary>
    public static async Task<(GarnetPartitionBlobStorage Storage, GarnetJournalPartitionHost Host)> StartAsync(
        BlobContainerClient container,
        string storageLocation,
        long epoch,
        CancellationToken cancellationToken)
    {
        var storage = await GarnetPartitionBlobStorage.OpenAsync(container, storageLocation, epoch, logger: null, cancellationToken);
        var host = GarnetJournalPartitionHost.Start(
            new GarnetJournalPartitionSettings { BlobStorage = storage, LogMemorySize = "64m", IndexMemorySize = "1m" },
            loggerFactory: null);
        return (storage, host);
    }

    /// <summary>Creates a router for a one-partition map whose partition listens at <paramref name="endPoint"/>.</summary>
    public static async Task<GarnetJournalRouter> CreateRouterAsync(IRangeMetaStore store, RangeMap map, IPEndPoint endPoint, TimeSpan operationTimeout, CancellationToken cancellationToken)
    {
        var routed = map with { Partitions = map.Partitions.SetItem(GarnetTestRouting.P0, map.Partitions[GarnetTestRouting.P0] with { Endpoint = GarnetJournalPartitionManager.FormatEndpoint(endPoint) }) };
        await GarnetJournalPartitionManager.InitializeAsync(endPoint, GarnetTestRouting.P0, routed, cancellationToken);
        return new GarnetJournalRouter(store, new RangeMapSnapshot(1, routed), operationTimeout);
    }

    public static RangeMap CreateMap(int bucketCount = 16)
        => RangeMap.CreateUniform(bucketCount, [new(GarnetTestRouting.P0, new PartitionHosting("test", 1, "p0"))]);
}
