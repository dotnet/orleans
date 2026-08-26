using System.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class VolatileJournalStorageProviderTests
{
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
            provider.ListAsync(JournalId.Create("named", "logs"), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.Equal([idA, idChild, idB], listed);

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
                provider.ListAsync(storageId, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken));

        await storage.DeleteAsync(TestContext.Current.CancellationToken);

        Assert.Null(await storage.GetMetadataAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await ToListAsync(
            provider.ListAsync(storageId, TestContext.Current.CancellationToken),
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

    private static async Task<List<T>> ToListAsync<T>(
        IAsyncEnumerable<T> source,
        CancellationToken cancellationToken)
    {
        var result = new List<T>();
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            result.Add(item);
        }

        return result;
    }
}
