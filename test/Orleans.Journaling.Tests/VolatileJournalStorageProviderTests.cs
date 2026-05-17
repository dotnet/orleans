using System.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

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
        var created = await storageA.CreateIfNotExistsAsync(new Dictionary<string, string> { ["owner"] = "one" });
        await provider.CreateStorage(idB).CreateIfNotExistsAsync();
        await provider.CreateStorage(idChild).CreateIfNotExistsAsync();
        await provider.CreateStorage(other).CreateIfNotExistsAsync();

        Assert.True(created);
        var metadata = await storageA.GetMetadataAsync();
        Assert.NotNull(metadata);
        Assert.NotNull(metadata.ETag);
        Assert.Equal("one", metadata.Properties["owner"]);

        var alreadyExists = await storageA.CreateIfNotExistsAsync(new Dictionary<string, string> { ["owner"] = "two" });
        Assert.False(alreadyExists);
        Assert.Equal("one", (await storageA.GetMetadataAsync())!.Properties["owner"]);

        var listed = await ToListAsync(provider.ListAsync(JournalId.Create("named", "logs")));
        Assert.Equal([idA, idChild, idB], listed);

        Assert.NotNull(await provider.CreateStorage(idB).GetMetadataAsync());
        Assert.Null(await provider.CreateStorage(JournalId.Create("named", "missing")).GetMetadataAsync());
    }

    [Fact]
    public async Task UpdateMetadata_UsesETagCasAndReportsNoChange()
    {
        var provider = new VolatileJournalStorageProvider();
        var storage = provider.CreateStorage(JournalId.Create("named", "properties", "cas"));
        Assert.True(await storage.CreateIfNotExistsAsync(new Dictionary<string, string>
        {
            ["keep"] = "1",
            ["remove"] = "2"
        }));
        var original = (await storage.GetMetadataAsync())!;

        var updated = await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["keep"] = "3", ["add"] = "4" },
            ["remove"],
            original.ETag);

        Assert.NotNull(updated);
        Assert.NotEqual(original.ETag, updated.ETag);
        Assert.Equal("3", updated.Properties["keep"]);
        Assert.Equal("4", updated.Properties["add"]);
        Assert.False(updated.Properties.ContainsKey("remove"));

        var stale = await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["keep"] = "5" },
            remove: null,
            original.ETag);
        Assert.Null(stale);
        Assert.Equal("3", (await storage.GetMetadataAsync())!.Properties["keep"]);

        var noChange = await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["keep"] = "3" },
            remove: null,
            updated.ETag);
        Assert.NotNull(noChange);
        Assert.Equal(updated.ETag, noChange.ETag);
    }

    [Fact]
    public async Task StorageOperationsUpdateMetadataAndDeleteRemovesStorage()
    {
        var provider = new VolatileJournalStorageProvider();
        var storageId = JournalId.Create("named", "conditional", "storage");
        var storage = provider.CreateStorage(storageId);

        Assert.Null(await storage.GetMetadataAsync());

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        var appendProperties = await storage.GetMetadataAsync();
        Assert.NotNull(appendProperties);
        Assert.NotNull(appendProperties.ETag);

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);
        var replaceProperties = await storage.GetMetadataAsync();
        Assert.NotNull(replaceProperties);
        Assert.NotEqual(appendProperties.ETag, replaceProperties.ETag);

        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);
        var finalProperties = await storage.GetMetadataAsync();
        Assert.NotNull(finalProperties);
        Assert.NotEqual(replaceProperties.ETag, finalProperties.ETag);

        Assert.Equal([storageId], await ToListAsync(provider.ListAsync(storageId)));

        await storage.DeleteAsync(CancellationToken.None);

        Assert.Null(await storage.GetMetadataAsync());
        Assert.Empty(await ToListAsync(provider.ListAsync(storageId)));
    }

    [Fact]
    public async Task CallerCannotSetProviderOwnedProperties()
    {
        var provider = new VolatileJournalStorageProvider();
        var storage = provider.CreateStorage(JournalId.Create("named", "reserved", "properties"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["$owner"] = "provider" }).AsTask());

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.UpdateMetadataAsync(new Dictionary<string, string> { ["$owner"] = "provider" }).AsTask());
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }
}
