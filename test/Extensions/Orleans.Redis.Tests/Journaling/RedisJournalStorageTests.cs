using System.Buffers;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Storage;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

#nullable enable

namespace Tester.Redis.Journaling;

[TestCategory("BVT")]
public sealed class RedisJournalStorageTests
{
    [SkippableFact]
    public async Task AppendAndRead_RoundTripsBytesAndMetadata()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync();
        var provider = context.Provider;
        var storage = provider.CreateStorage(JournalId.Create("redis", "append"));
        Assert.True(await storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["owner"] = "test" }));

        await storage.AppendAsync(new ReadOnlySequence<byte>([1, 2]), CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([3, 4, 5]), CancellationToken.None);

        var metadata = await storage.GetMetadataAsync();
        Assert.NotNull(metadata);
        Assert.Equal("test", metadata.Properties["owner"]);
        Assert.Equal(new JournaledStateManagerOptions().JournalFormatKey, metadata.Format);

        var consumer = new CapturingJournalStorageConsumer();
        await provider.CreateStorage(JournalId.Create("redis", "append")).ReadAsync(consumer, CancellationToken.None);

        Assert.True(consumer.IsCompleted);
        Assert.Equal([1, 2, 3, 4, 5], consumer.Bytes.ToArray());
        Assert.Equal(metadata.Format, consumer.Metadata?.Format);
    }

    [SkippableFact]
    public async Task ReplaceCatalogAndDelete_UseJournalIds()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync();
        var provider = context.Provider;
        var idA = JournalId.Create("redis", "catalog", "a");
        var idB = JournalId.Create("redis", "catalog", "b");
        var child = JournalId.Create("redis", "catalog", "a", "child");
        var other = JournalId.Create("redis", "other");

        await provider.CreateStorage(idA).ReplaceAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await provider.CreateStorage(idB).CreateIfNotExistsAsync();
        await provider.CreateStorage(child).AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);
        await provider.CreateStorage(other).CreateIfNotExistsAsync();

        var listed = await ToListAsync(provider.ListAsync(JournalId.Create("redis", "catalog")));
        Assert.Equal([idA, child, idB], listed);

        await provider.CreateStorage(idA).DeleteAsync(CancellationToken.None);

        listed = await ToListAsync(provider.ListAsync(JournalId.Create("redis", "catalog")));
        Assert.Equal([child, idB], listed);
    }

    [SkippableFact]
    public async Task UpdateMetadata_UsesETagCasAndPreservesNoChangeETag()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync();
        var provider = context.Provider;
        var storage = provider.CreateStorage(JournalId.Create("redis", "metadata"));
        Assert.True(await storage.CreateIfNotExistsAsync(new Dictionary<string, string>
        {
            ["keep"] = "1",
            ["remove"] = "2",
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

        var noChange = await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["keep"] = "3" },
            remove: null,
            updated.ETag);
        Assert.NotNull(noChange);
        Assert.Equal(updated.ETag, noChange.ETag);
    }

    [SkippableFact]
    public async Task StaleAppend_ThrowsInconsistentStateException()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync();
        var provider = context.Provider;
        var id = JournalId.Create("redis", "stale");
        var first = provider.CreateStorage(id);
        var second = provider.CreateStorage(id);

        await first.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        var consumer = new CapturingJournalStorageConsumer();
        await second.ReadAsync(consumer, CancellationToken.None);
        await first.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => second.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None).AsTask());
    }

    [SkippableFact]
    public async Task CallerCannotSetProviderOwnedProperties()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync();
        var provider = context.Provider;
        var storage = provider.CreateStorage(JournalId.Create("redis", "reserved"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.CreateIfNotExistsAsync(new Dictionary<string, string> { ["$owner"] = "provider" }).AsTask());

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.UpdateMetadataAsync(new Dictionary<string, string> { ["format"] = "provider" }).AsTask());
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

    private sealed class RedisJournalStorageTestContext : IAsyncDisposable
    {
        private readonly string _keyPrefix;
        private readonly ConnectionMultiplexer _multiplexer;

        private RedisJournalStorageTestContext(
            RedisJournalStorageProvider provider,
            string keyPrefix,
            ConnectionMultiplexer multiplexer)
        {
            Provider = provider;
            _keyPrefix = keyPrefix;
            _multiplexer = multiplexer;
        }

        public RedisJournalStorageProvider Provider { get; }

        public static async Task<RedisJournalStorageTestContext> CreateAsync()
        {
            var keyPrefix = $"orleans-tests/journaling/{Guid.NewGuid():N}";
            var multiplexer = await ConnectionMultiplexer.ConnectAsync(TestDefaultConfiguration.RedisConnectionString);
            var options = new RedisJournalStorageOptions
            {
                ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString),
                CreateMultiplexer = _ => Task.FromResult(((IConnectionMultiplexer)multiplexer, true)),
                KeyPrefix = keyPrefix,
                ReadChunkSize = 2,
            };

            var provider = new RedisJournalStorageProvider(
                Options.Create(options),
                Options.Create(new ClusterOptions { ServiceId = $"redis-journaling-tests-{Guid.NewGuid():N}" }),
                Options.Create(new JournaledStateManagerOptions()));

            var lifecycle = new SiloLifecycleSubject(Microsoft.Extensions.Logging.Abstractions.NullLogger<SiloLifecycleSubject>.Instance);
            provider.Participate(lifecycle);
            await lifecycle.OnStart(CancellationToken.None);
            return new(provider, keyPrefix, multiplexer);
        }

        public async ValueTask DisposeAsync()
        {
            var database = _multiplexer.GetDatabase();
            await foreach (var key in _multiplexer.GetServer(_multiplexer.GetEndPoints()[0]).KeysAsync(pattern: $"{_keyPrefix}:*"))
            {
                await database.KeyDeleteAsync(key);
            }

            await _multiplexer.CloseAsync();
            _multiplexer.Dispose();
        }
    }

    private sealed class CapturingJournalStorageConsumer : IJournalStorageConsumer
    {
        public List<byte> Bytes { get; } = [];

        public IJournalMetadata? Metadata { get; private set; }

        public bool IsCompleted { get; private set; }

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            Metadata ??= metadata;
            if (buffer.IsCompleted)
            {
                IsCompleted = true;
            }

            var segment = new byte[buffer.Length];
            buffer.Read(segment);
            Bytes.AddRange(segment);
        }
    }
}
