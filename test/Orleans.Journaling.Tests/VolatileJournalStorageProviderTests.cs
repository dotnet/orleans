using System.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class VolatileJournalStorageProviderTests
{
    [Fact]
    public async Task CreateIfNotExists_ListAndGetPropertiesUseNamedStorageIds()
    {
        var provider = new VolatileJournalStorageProvider();
        var idA = JournalStorageId.Create("named", "logs", "a");
        var idB = JournalStorageId.Create("named", "logs", "b");
        var idChild = JournalStorageId.Create("named", "logs", "a", "child");
        var other = JournalStorageId.Create("named", "other", "a");

        var created = await provider.CreateIfNotExistsAsync(idA, new Dictionary<string, string> { ["owner"] = "one" });
        await provider.CreateIfNotExistsAsync(idB);
        await provider.CreateIfNotExistsAsync(idChild);
        await provider.CreateIfNotExistsAsync(other);

        Assert.Equal(JournalStorageCreateStatus.Created, created.Status);
        Assert.NotNull(created.Properties);
        Assert.NotNull(created.Properties.ETag);
        Assert.Equal("one", created.Properties.Values["owner"]);

        var alreadyExists = await provider.CreateIfNotExistsAsync(idA, new Dictionary<string, string> { ["owner"] = "one" });
        Assert.Equal(JournalStorageCreateStatus.AlreadyExists, alreadyExists.Status);
        Assert.Equal("one", alreadyExists.Properties!.Values["owner"]);

        var conflict = await provider.CreateIfNotExistsAsync(idA, new Dictionary<string, string> { ["owner"] = "two" });
        Assert.Equal(JournalStorageCreateStatus.Conflict, conflict.Status);
        Assert.Equal("one", conflict.Properties!.Values["owner"]);

        var listed = await ToListAsync(provider.ListAsync(JournalStoragePrefix.Create("named", "logs")));
        Assert.Equal([idA, idChild, idB], listed);

        Assert.NotNull(await provider.GetPropertiesAsync(idB));
        Assert.Null(await provider.GetPropertiesAsync(JournalStorageId.Create("named", "missing")));
    }

    [Fact]
    public async Task UpdateProperties_UsesETagCasAndReportsNoChange()
    {
        var provider = new VolatileJournalStorageProvider();
        var storageId = JournalStorageId.Create("named", "properties", "cas");
        var created = await provider.CreateIfNotExistsAsync(
            storageId,
            new Dictionary<string, string>
            {
                ["keep"] = "1",
                ["remove"] = "2"
            });
        var original = created.Properties!;

        var updated = await provider.UpdatePropertiesAsync(
            storageId,
            new JournalStoragePropertiesUpdate(
                new Dictionary<string, string> { ["keep"] = "3", ["add"] = "4" },
                ["remove"]),
            original.ETag);

        Assert.Equal(JournalStoragePropertiesUpdateStatus.Updated, updated.Status);
        Assert.NotEqual(original.ETag, updated.Properties!.ETag);
        Assert.Equal("3", updated.Properties.Values["keep"]);
        Assert.Equal("4", updated.Properties.Values["add"]);
        Assert.False(updated.Properties.Values.ContainsKey("remove"));

        var stale = await provider.UpdatePropertiesAsync(
            storageId,
            JournalStoragePropertiesUpdate.SetProperty("keep", "5"),
            original.ETag);
        Assert.Equal(JournalStoragePropertiesUpdateStatus.ETagMismatch, stale.Status);
        Assert.Equal(updated.Properties.ETag, stale.Properties!.ETag);
        Assert.Equal("3", stale.Properties.Values["keep"]);

        var noChange = await provider.UpdatePropertiesAsync(
            storageId,
            JournalStoragePropertiesUpdate.SetProperty("keep", "3"),
            updated.Properties.ETag);
        Assert.Equal(JournalStoragePropertiesUpdateStatus.NoChange, noChange.Status);
        Assert.Equal(updated.Properties.ETag, noChange.Properties!.ETag);
    }

    [Fact]
    public async Task StorageOperationsUpdateCatalogPropertiesAndDeleteRemovesStorage()
    {
        var provider = new VolatileJournalStorageProvider();
        var storageId = JournalStorageId.Create("named", "conditional", "storage");
        var storage = provider.Create(storageId);

        Assert.Null(await provider.GetPropertiesAsync(storageId));

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        var appendProperties = await provider.GetPropertiesAsync(storageId);
        Assert.NotNull(appendProperties);
        Assert.NotNull(appendProperties.ETag);

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);
        var replaceProperties = await provider.GetPropertiesAsync(storageId);
        Assert.NotNull(replaceProperties);
        Assert.NotEqual(appendProperties.ETag, replaceProperties.ETag);

        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);
        var finalProperties = await provider.GetPropertiesAsync(storageId);
        Assert.NotNull(finalProperties);
        Assert.NotEqual(replaceProperties.ETag, finalProperties.ETag);

        Assert.Equal([storageId], await ToListAsync(provider.ListAsync(storageId.AsPrefix())));

        await storage.DeleteAsync(CancellationToken.None);

        Assert.Null(await provider.GetPropertiesAsync(storageId));
        Assert.Empty(await ToListAsync(provider.ListAsync(storageId.AsPrefix())));
    }

    [Fact]
    public async Task CallerCannotSetProviderOwnedProperties()
    {
        var provider = new VolatileJournalStorageProvider();
        var storageId = JournalStorageId.Create("named", "reserved", "properties");

        await Assert.ThrowsAsync<ArgumentException>(
            () => provider.CreateIfNotExistsAsync(
                storageId,
                new Dictionary<string, string> { [JournalStoragePropertyNames.ProviderReservedPrefix + "owner"] = "provider" }).AsTask());

        Assert.Throws<ArgumentException>(
            () => JournalStoragePropertiesUpdate.SetProperty(JournalStoragePropertyNames.ProviderReservedPrefix + "owner", "provider"));
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
