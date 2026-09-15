using System.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class VolatileJournalStorageProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAsync_MetadataProjectionFlagIsSnapshottedAtEnumerationStart(bool includeMetadata)
    {
        var provider = new VolatileJournalStorageProvider();
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var id in new[] { "a", "b" })
        {
            await provider.CreateStorage(new(id)).CreateIfNotExistsAsync(
                new Dictionary<string, string> { ["owner"] = id }, cancellationToken);
        }

        var options = new ListOptions { IncludeMetadata = !includeMetadata };
        var listing = provider.ListAsync(options, cancellationToken);
        options.IncludeMetadata = includeMetadata;
        await using var enumerator = listing.GetAsyncEnumerator(cancellationToken);
        foreach (var id in new[] { "a", "b" })
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new JournalId(id), enumerator.Current.Id);
            if (includeMetadata)
            {
                var metadata = Assert.IsType<JournalMetadata>(enumerator.Current.Metadata);
                var stored = await provider.CreateStorage(new(id)).GetMetadataAsync(cancellationToken);
                Assert.NotNull(stored);
                Assert.Equal(stored.Format, metadata.Format);
                Assert.Equal(stored.ETag, metadata.ETag);
                Assert.Equal(stored.Properties, metadata.Properties);
            }
            else
            {
                Assert.Null(enumerator.Current.Metadata);
            }

            options.IncludeMetadata = !includeMetadata;
        }

        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task ListAsync_MetadataSnapshotPreservesPropertiesAndRejectsStaleConditionalUpdate()
    {
        var provider = new VolatileJournalStorageProvider();
        var id = new JournalId("metadata/snapshot");
        var storage = provider.CreateStorage(id);
        var cancellationToken = TestContext.Current.CancellationToken;
        await storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["owner"] = "first" }, cancellationToken);
        await using var enumerator = provider.ListAsync(
            new() { IncludeMetadata = true }, cancellationToken).GetAsyncEnumerator(cancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(id, enumerator.Current.Id);
        var snapshot = Assert.IsType<JournalMetadata>(enumerator.Current.Metadata);
        Assert.NotNull(snapshot.ETag);

        var updated = await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["owner"] = "second", ["added"] = "value" },
            expectedETag: snapshot.ETag, cancellationToken: cancellationToken);
        Assert.NotNull(updated);
        Assert.NotEqual(snapshot.ETag, updated.ETag);
        Assert.Equal(new Dictionary<string, string> { ["owner"] = "first" }, snapshot.Properties);
        Assert.Null(await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["owner"] = "stale" },
            expectedETag: snapshot.ETag, cancellationToken: cancellationToken));
        var current = await storage.GetMetadataAsync(cancellationToken);
        Assert.NotNull(current);
        Assert.Equal(updated.ETag, current.ETag);
        Assert.Equal(updated.Properties, current.Properties);
    }

    [Fact]
    public async Task ListAsync_UsesRawPrefixAndSnapshotsInclusiveRange()
    {
        var provider = new VolatileJournalStorageProvider();
        string[] ids = ["jobs/20260908-a", "jobs/20260909-a", "jobs/20260909-b", "jobs/20260909-c", "jobs/20260910-a", "other"];
        foreach (var value in ids)
        {
            await provider.CreateStorage(new(value)).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        var options = new ListOptions { Prefix = new("other") };
        var listing = provider.ListAsync(options, TestContext.Current.CancellationToken);
        options.Prefix = new("jobs/20260909");
        options.MinId = new("jobs/20260909-b");
        options.MaxId = new("jobs/20260909-c");
        await using var enumerator = listing.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("jobs/20260909-b", enumerator.Current.Id.Value);

        options.Prefix = new("other");
        options.MinId = default;
        options.MaxId = default;
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("jobs/20260909-c", enumerator.Current.Id.Value);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal([new JournalId("other")], await ToListAsync(listing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ListAsync_RangeIndexIncludesRecreatedStoresAndExcludesDeletedStores()
    {
        var provider = new VolatileJournalStorageProvider();
        var lower = new JournalId("range/b");
        var upper = new JournalId("range/d");
        foreach (var name in new[] { "range/a", "range/b", "range/c", "range/d", "range/e" })
        {
            await provider.CreateStorage(new(name)).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        var deleted = provider.CreateStorage(new("range/c"));
        await deleted.DeleteAsync(TestContext.Current.CancellationToken);
        var options = new ListOptions { MinId = lower, MaxId = upper };
        Assert.Equal([lower, upper], await ToListAsync(provider.ListAsync(options, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));

        await deleted.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([lower, new JournalId("range/c"), upper], await ToListAsync(provider.ListAsync(options, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        options.MinId = new("z");
        Assert.Empty(await ToListAsync(provider.ListAsync(options, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateIfNotExists_ListAndGetMetadataUseJournalIds()
    {
        var provider = new VolatileJournalStorageProvider();
        var idA = JournalId.Create("named", "logs", "a");
        var idB = JournalId.Create("named", "logs", "b");
        var idChild = JournalId.Create("named", "logs", "a", "child");
        var other = JournalId.Create("named", "other", "a");

        var storageA = provider.CreateStorage(idA);
        var created = await storageA.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["owner"] = "one" },
            TestContext.Current.CancellationToken);
        await provider.CreateStorage(idB).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await provider.CreateStorage(idChild).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await provider.CreateStorage(other).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(created);
        var metadata = await storageA.GetMetadataAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        Assert.NotNull(metadata.ETag);
        Assert.Equal("one", metadata.Properties["owner"]);

        var alreadyExists = await storageA.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["owner"] = "two" },
            TestContext.Current.CancellationToken);
        Assert.False(alreadyExists);
        Assert.Equal("one", (await storageA.GetMetadataAsync(TestContext.Current.CancellationToken))!.Properties["owner"]);

        var listed = await ToListAsync(
            provider.ListAsync(new() { Prefix = JournalId.Create("named", "logs") }, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.Equal([idA, idChild, idB], listed.OrderBy(id => id.Value, StringComparer.Ordinal));

        Assert.NotNull(await provider.CreateStorage(idB).GetMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Null(await provider.CreateStorage(JournalId.Create("named", "missing")).GetMetadataAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateMetadata_UsesETagCasAndReportsNoChange()
    {
        var provider = new VolatileJournalStorageProvider();
        var storage = provider.CreateStorage(JournalId.Create("named", "properties", "cas"));
        Assert.True(await storage.CreateIfNotExistsAsync(
            new Dictionary<string, string>
            {
                ["keep"] = "1",
                ["remove"] = "2"
            },
            TestContext.Current.CancellationToken));
        var original = (await storage.GetMetadataAsync(TestContext.Current.CancellationToken))!;

        var updated = await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["keep"] = "3", ["add"] = "4" },
            ["remove"],
            original.ETag,
            TestContext.Current.CancellationToken);

        Assert.NotNull(updated);
        Assert.NotEqual(original.ETag, updated.ETag);
        Assert.Equal("3", updated.Properties["keep"]);
        Assert.Equal("4", updated.Properties["add"]);
        Assert.False(updated.Properties.ContainsKey("remove"));

        var stale = await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["keep"] = "5" },
            remove: null,
            original.ETag,
            TestContext.Current.CancellationToken);
        Assert.Null(stale);
        Assert.Equal("3", (await storage.GetMetadataAsync(TestContext.Current.CancellationToken))!.Properties["keep"]);

        var noChange = await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["keep"] = "3" },
            remove: null,
            updated.ETag,
            TestContext.Current.CancellationToken);
        Assert.NotNull(noChange);
        Assert.Equal(updated.ETag, noChange.ETag);
    }

    [Fact]
    public async Task StorageOperationsUpdateMetadataAndDeleteRemovesStorage()
    {
        var provider = new VolatileJournalStorageProvider();
        var storageId = JournalId.Create("named", "conditional", "storage");
        var storage = provider.CreateStorage(storageId);

        Assert.Null(await storage.GetMetadataAsync(TestContext.Current.CancellationToken));

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var appendProperties = await storage.GetMetadataAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(appendProperties);
        Assert.NotNull(appendProperties.ETag);

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);
        var replaceProperties = await storage.GetMetadataAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(replaceProperties);
        Assert.NotEqual(appendProperties.ETag, replaceProperties.ETag);

        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), TestContext.Current.CancellationToken);
        var finalProperties = await storage.GetMetadataAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(finalProperties);
        Assert.NotEqual(replaceProperties.ETag, finalProperties.ETag);

        Assert.Equal(
            [storageId],
            await ToListAsync(
                provider.ListAsync(new() { Prefix = storageId }, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken));

        await storage.DeleteAsync(TestContext.Current.CancellationToken);

        Assert.Null(await storage.GetMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await ToListAsync(
            provider.ListAsync(new() { Prefix = storageId }, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CallerCannotSetProviderOwnedProperties()
    {
        var provider = new VolatileJournalStorageProvider();
        var storage = provider.CreateStorage(JournalId.Create("named", "reserved", "properties"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.CreateIfNotExistsAsync(
                new Dictionary<string, string> { ["$owner"] = "provider" },
                TestContext.Current.CancellationToken).AsTask());

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.UpdateMetadataAsync(
                new Dictionary<string, string> { ["$owner"] = "provider" },
                cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    private static async Task<List<JournalId>> ToListAsync(
        IAsyncEnumerable<JournalCatalogEntry> source,
        CancellationToken cancellationToken)
    {
        var result = new List<JournalId>();
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            result.Add(item.Id);
        }

        return result;
    }
}
