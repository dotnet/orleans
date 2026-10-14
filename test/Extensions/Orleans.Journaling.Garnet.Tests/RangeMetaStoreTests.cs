using Azure.Storage.Blobs;
using Orleans.Journaling.Ranges;
using TestExtensions;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Tests the compare-and-swap contract of <see cref="IRangeMetaStore"/> implementations.
/// </summary>
public abstract class RangeMetaStoreTests
{
    private static readonly PartitionId P0 = new("p0");

    protected static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    protected abstract IRangeMetaStore CreateStore();

    private static RangeMap CreateMap(long epoch = 0, string? endpoint = null)
        => RangeMap.CreateUniform(16, [new(P0, new PartitionHosting("host", epoch, "p0", endpoint))]);

    [Fact]
    public async Task EmptyStore_ReadsNoMapAndAcceptsOnlyTheFirstVersion()
    {
        var store = CreateStore();
        Assert.Null(await store.ReadAsync(CancellationToken));

        var rejected = await store.TryWriteAsync(CreateMap(), expectedVersion: 1, CancellationToken);
        Assert.False(rejected.Written);
        Assert.Null(rejected.Current);

        var written = await store.TryWriteAsync(CreateMap(), expectedVersion: 0, CancellationToken);
        Assert.True(written.Written);
        Assert.Equal(new RangeMapSnapshot(1, CreateMap()), written.Current);
        Assert.Equal(written.Current, await store.ReadAsync(CancellationToken));
    }

    [Fact]
    public async Task StaleWrite_FailsAndReturnsTheStoredMap()
    {
        var store = CreateStore();
        await store.TryWriteAsync(CreateMap(), 0, CancellationToken);
        Assert.True((await store.TryWriteAsync(CreateMap(epoch: 1), 1, CancellationToken)).Written);

        foreach (var stale in new long[] { 0, 1, 3 })
        {
            var result = await store.TryWriteAsync(CreateMap(epoch: 9), stale, CancellationToken);
            Assert.False(result.Written);
            Assert.Equal(new RangeMapSnapshot(2, CreateMap(epoch: 1)), result.Current);
        }

        Assert.Equal(new RangeMapSnapshot(2, CreateMap(epoch: 1)), await store.ReadAsync(CancellationToken));
    }

    [Fact]
    public async Task InvalidWrites_AreRejected()
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryWriteAsync(CreateMap() with { BucketCount = 12 }, 0, CancellationToken).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.TryWriteAsync(CreateMap(), -1, CancellationToken).AsTask());
        Assert.Null(await store.ReadAsync(CancellationToken));
    }

    /// <summary>
    /// Concurrent writers each repeatedly read the map, increment its epoch, and write it back. Every successful write
    /// applies exactly one increment, so the final epoch equals the number of successes and the final version.
    /// </summary>
    [Fact]
    public async Task ConcurrentWriters_ApplyEachSuccessfulWriteExactlyOnce()
    {
        const int Writers = 8;
        const int IncrementsPerWriter = 10;
        var store = CreateStore();
        await store.TryWriteAsync(CreateMap(), 0, CancellationToken);
        var conflicts = 0;

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => Task.Run(async () =>
        {
            var current = (await store.ReadAsync(CancellationToken))!;
            for (var i = 0; i < IncrementsPerWriter;)
            {
                var hosting = current.Map.Partitions[P0];
                var next = current.Map with { Partitions = current.Map.Partitions.SetItem(P0, hosting with { Epoch = hosting.Epoch + 1, Endpoint = $"writer-{writer}:{i}" }) };
                var result = await store.TryWriteAsync(next, current.Version, CancellationToken);
                if (result.Written)
                {
                    Assert.Equal(current.Version + 1, result.Current!.Version);
                    i++;
                }
                else
                {
                    Interlocked.Increment(ref conflicts);
                    Assert.True(result.Current!.Version > current.Version);
                }

                current = result.Current;
            }
        }, CancellationToken)));

        var final = (await store.ReadAsync(CancellationToken))!;
        Assert.Equal(1 + Writers * IncrementsPerWriter, final.Version);
        Assert.Equal(Writers * IncrementsPerWriter, final.Map.Partitions[P0].Epoch);
        TestContext.Current.TestOutputHelper?.WriteLine($"{conflicts} conflicting writes.");
    }
}

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class InMemoryRangeMetaStoreTests : RangeMetaStoreTests
{
    protected override IRangeMetaStore CreateStore() => new InMemoryRangeMetaStore();
}

[TestSuite("Functional")]
[TestProvider("AzureStorage")]
[TestCategory("AzureStorage"), TestCategory("Functional")]
public sealed class AzureBlobRangeMetaStoreTests : RangeMetaStoreTests, IAsyncDisposable
{
    private readonly List<BlobContainerClient> _containers = [];

    public AzureBlobRangeMetaStoreTests()
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

    // A new container per store exercises container creation on the first write.
    protected override IRangeMetaStore CreateStore() => new AzureBlobRangeMetaStore(CreateContainer().GetBlobClient("range-map.json"));

    [Fact]
    public async Task CorruptDocument_IsReportedAsInvalidData()
    {
        var container = CreateContainer();
        await container.CreateIfNotExistsAsync(cancellationToken: CancellationToken);
        var blob = container.GetBlobClient("range-map.json");
        await blob.UploadAsync(BinaryData.FromString("""{"format":1}"""), CancellationToken);
        var store = new AzureBlobRangeMetaStore(blob);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(CancellationToken).AsTask());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in _containers)
        {
            await container.DeleteIfExistsAsync();
        }
    }

    private BlobContainerClient CreateContainer()
    {
        var service = TestDefaultConfiguration.UseAadAuthentication
            ? new BlobServiceClient(TestDefaultConfiguration.DataBlobUri, TestDefaultConfiguration.TokenCredential)
            : new BlobServiceClient(TestDefaultConfiguration.DataConnectionString);
        var container = service.GetBlobContainerClient($"garnet-range-map-{Guid.NewGuid():N}");
        _containers.Add(container);
        return container;
    }
}
