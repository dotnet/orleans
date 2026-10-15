using Azure.Storage.Blobs;
using Orleans.Journaling.Ranges;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Tests the validation of partitions' shared-storage locations: a takeover fences every blob under its location, so
/// locations must be disjoint.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetPartitionStorageLocationTests
{
    [Theory]
    [InlineData("p0", "p1")]
    [InlineData("a", "ab")]
    [InlineData("a/b", "a/c")]
    [InlineData("a-b", "a/b")]
    public void DisjointLocations_AreAccepted(string first, string second)
        => GarnetPartitionBlobStorage.ValidateStorageLocations(Locations(first, second));

    [Theory]
    [InlineData("a", "a/b")]
    [InlineData("a/b", "a")]
    [InlineData("a", "a")]
    [InlineData("a/", "a")]
    [InlineData("a\\b", "a/b")]
    [InlineData("x/a", "x/a/b/c")]
    public void OverlappingLocations_AreRejected(string first, string second)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => GarnetPartitionBlobStorage.ValidateStorageLocations(Locations(first, second, "z")));
        Assert.Contains("overlapping", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("../a")]
    public void InvalidLocations_AreRejected(string location)
        => Assert.Throws<InvalidOperationException>(() => GarnetPartitionBlobStorage.ValidateStorageLocations(Locations(location)));

    [Fact]
    public async Task PartitionManager_RefusesToHostPartitionsWithOverlappingLocations()
    {
        var store = new InMemoryRangeMetaStore();
        var map = RangeMap.CreateUniform(16, Locations("a", "a/b"));
        await store.TryWriteAsync(map, 0, TestContext.Current.CancellationToken);
        var options = new GarnetJournalStorageOptions
        {
            MetaStore = store,
            PartitionBlobContainer = new BlobContainerClient("UseDevelopmentStorage=true", "unused"),
        };

        // Validation precedes any takeover, so the container is never touched.
        var view = (await store.ReadAsync(TestContext.Current.CancellationToken))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => GarnetJournalPartitionManager.StartAsync(
            options, "host", store, view, loggerFactory: null, TestContext.Current.CancellationToken));
    }

    private static KeyValuePair<PartitionId, PartitionHosting>[] Locations(params string[] locations)
        => [.. locations.Select((location, index) => KeyValuePair.Create(new PartitionId($"p{index}"), new PartitionHosting("host", 0, location)))];
}
