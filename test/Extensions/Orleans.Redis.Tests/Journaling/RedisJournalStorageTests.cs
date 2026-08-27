using System.Buffers;
using System.Text;
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

[TestSuite("BVT")]
[TestProvider("Redis")]
[TestCategory("BVT")]
public sealed class RedisJournalStorageTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("prefix\0suffix")]
    public void OptionsValidator_InvalidKeyPrefix_Throws(string keyPrefix)
    {
        var options = new RedisJournalStorageOptions
        {
            ConfigurationOptions = ConfigurationOptions.Parse("localhost"),
            KeyPrefix = keyPrefix,
        };

        var exception = Assert.Throws<OrleansConfigurationException>(
            () => new RedisJournalStorageOptionsValidator(options).ValidateConfiguration());
        Assert.Contains(nameof(RedisJournalStorageOptions.KeyPrefix), exception.Message);
    }

    [Fact]
    public async Task AppendAndRead_RoundTripsBytesAndMetadata()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var provider = context.Provider;
        var storage = provider.CreateStorage(JournalId.Create("redis", "append"));
        Assert.True(await storage.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["owner"] = "test" },
            TestContext.Current.CancellationToken));

        await storage.AppendAsync(new ReadOnlySequence<byte>([1, 2]), TestContext.Current.CancellationToken);
        await storage.AppendAsync(CreateSequence([3, 4], [5]), TestContext.Current.CancellationToken);

        var metadata = await storage.GetMetadataAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        Assert.Equal("test", metadata.Properties["owner"]);
        Assert.Equal(new JournaledStateManagerOptions().JournalFormatKey, metadata.Format);

        var consumer = new CapturingJournalStorageConsumer();
        await provider.CreateStorage(JournalId.Create("redis", "append")).ReadAsync(consumer, TestContext.Current.CancellationToken);

        Assert.True(consumer.IsCompleted);
        Assert.Equal([1, 2, 3, 4, 5], consumer.Bytes.ToArray());
        Assert.Equal(metadata.Format, consumer.Metadata?.Format);
    }

    [Fact]
    public async Task ReplaceListAndDelete_UseJournalIds()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var provider = context.Provider;
        var idA = JournalId.Create("redis", "list", "a");
        var idB = JournalId.Create("redis", "list", "b");
        var child = JournalId.Create("redis", "list", "a", "child");
        var other = JournalId.Create("redis", "other");

        await provider.CreateStorage(idA).ReplaceAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        await provider.CreateStorage(idB).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await provider.CreateStorage(child).AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);
        await provider.CreateStorage(other).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var listed = await ToListAsync(
            provider.ListAsync(JournalId.Create("redis", "list"), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.Equal([idA, child, idB], listed);

        await provider.CreateStorage(idA).DeleteAsync(TestContext.Current.CancellationToken);

        listed = await ToListAsync(
            provider.ListAsync(JournalId.Create("redis", "list"), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Assert.Equal([child, idB], listed);
    }

    [Fact]
    public async Task UpdateMetadata_UsesETagCasAndPreservesNoChangeETag()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var provider = context.Provider;
        var storage = provider.CreateStorage(JournalId.Create("redis", "metadata"));
        Assert.True(await storage.CreateIfNotExistsAsync(
            new Dictionary<string, string>
            {
                ["keep"] = "1",
                ["remove"] = "2",
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

        var noChange = await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["keep"] = "3" },
            remove: null,
            updated.ETag,
            TestContext.Current.CancellationToken);
        Assert.NotNull(noChange);
        Assert.Equal(updated.ETag, noChange.ETag);
    }

    [Fact]
    public async Task StaleAppend_ThrowsInconsistentStateException()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var provider = context.Provider;
        var id = JournalId.Create("redis", "stale");
        var first = provider.CreateStorage(id);
        var second = provider.CreateStorage(id);

        await first.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var consumer = new CapturingJournalStorageConsumer();
        await second.ReadAsync(consumer, TestContext.Current.CancellationToken);
        await first.AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => second.AppendAsync(new ReadOnlySequence<byte>([3]), TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task ColdAppendToExistingJournal_AppendsCurrentVersion()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var id = JournalId.Create("redis", "cold-append");
        await context.Provider.CreateStorage(id).AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);

        await context.Provider.CreateStorage(id).AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);

        var consumer = new CapturingJournalStorageConsumer();
        await context.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], consumer.Bytes);
    }

    [Fact]
    public async Task StaleAppendAfterDelete_DoesNotRecreateJournal()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var id = JournalId.Create("redis", "stale-after-delete");
        var stale = context.Provider.CreateStorage(id);
        await stale.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        await context.Provider.CreateStorage(id).DeleteAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InconsistentStateException>(
            () => stale.AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken).AsTask());
        Assert.Null(await context.Provider.CreateStorage(id).GetMetadataAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Read_PreservesIncompleteRecordsAcrossSegments()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(
            TestContext.Current.CancellationToken,
            options => options.ReadChunkSize = 2);
        var storage = context.Provider.CreateStorage(JournalId.Create("redis", "segmented-read"));
        await storage.ReplaceAsync(
            new ReadOnlySequence<byte>(Enumerable.Range(0, 10).Select(static value => (byte)value).ToArray()),
            TestContext.Current.CancellationToken);

        var consumer = new FixedRecordJournalStorageConsumer(recordSize: 5);
        await context.Provider.CreateStorage(JournalId.Create("redis", "segmented-read")).ReadAsync(
            consumer,
            TestContext.Current.CancellationToken);

        Assert.True(consumer.IsCompleted);
        Assert.Equal(2, consumer.Records.Count);
        Assert.Equal([0, 1, 2, 3, 4], consumer.Records[0]);
        Assert.Equal([5, 6, 7, 8, 9], consumer.Records[1]);
    }

    [Fact]
    public async Task Read_ObservesCancellationBetweenSegments()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(
            TestContext.Current.CancellationToken,
            options => options.ReadChunkSize = 2);
        var id = JournalId.Create("redis", "cancelled-read");
        await context.Provider.CreateStorage(id).ReplaceAsync(
            new ReadOnlySequence<byte>(Enumerable.Range(0, 10).Select(static value => (byte)value).ToArray()),
            TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var consumer = new CancellingJournalStorageConsumer(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Provider.CreateStorage(id).ReadAsync(consumer, cancellation.Token).AsTask());

        Assert.False(consumer.IsCompleted);
    }

    [Fact]
    public async Task MetadataOnlyUpdate_DoesNotInvalidateContentWriter()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var id = JournalId.Create("redis", "metadata-content-etag");
        var writer = context.Provider.CreateStorage(id);
        var metadataWriter = context.Provider.CreateStorage(id);
        await writer.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var metadata = await metadataWriter.GetMetadataAsync(TestContext.Current.CancellationToken);

        var updated = await metadataWriter.UpdateMetadataAsync(
            new Dictionary<string, string> { ["owner"] = "metadata-writer" },
            expectedETag: metadata!.ETag,
            cancellationToken: TestContext.Current.CancellationToken);
        await writer.AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken);

        Assert.NotNull(updated);
        var consumer = new CapturingJournalStorageConsumer();
        await context.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2], consumer.Bytes);
    }

    [Fact]
    public async Task ConcurrentReadAndReplace_ReturnsAtomicSnapshot()
    {
        TestUtils.CheckForRedis();
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = await RedisJournalStorageTestContext.CreateAsync(
            cancellationToken,
            options => options.ReadChunkSize = 8);
        var id = JournalId.Create("redis", "atomic-read");
        var writer = context.Provider.CreateStorage(id);
        var first = Enumerable.Repeat((byte)0x11, 512).ToArray();
        var second = Enumerable.Repeat((byte)0x22, 512).ToArray();
        await writer.ReplaceAsync(new ReadOnlySequence<byte>(first), cancellationToken);

        var replaceTask = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                var value = i % 2 == 0 ? second : first;
                await writer.ReplaceAsync(new ReadOnlySequence<byte>(value), cancellationToken);
            }
        }, cancellationToken);
        var readTasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            for (var i = 0; i < 25; i++)
            {
                var consumer = new CapturingJournalStorageConsumer();
                await context.Provider.CreateStorage(id).ReadAsync(consumer, cancellationToken);
                var bytes = consumer.Bytes.ToArray();
                Assert.True(bytes.SequenceEqual(first) || bytes.SequenceEqual(second));
            }
        });

        await Task.WhenAll(readTasks.Append(replaceTask));
    }

    [Fact]
    public async Task ConcurrentDeleteAndCreate_NeverLeavesMetadataWithoutData()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < 64; i++)
        {
            var id = JournalId.Create("redis", "delete-create", i.ToString());
            var deleting = context.Provider.CreateStorage(id);
            var creating = context.Provider.CreateStorage(id);
            var deleteTask = deleting.DeleteAsync(TestContext.Current.CancellationToken).AsTask();
            var appendTask = TryAppendAsync(creating, [1, 2, 3], TestContext.Current.CancellationToken);
            await Task.WhenAll(deleteTask, appendTask);

            var metadata = await context.Provider.CreateStorage(id).GetMetadataAsync(TestContext.Current.CancellationToken);
            if (metadata is not null)
            {
                var consumer = new CapturingJournalStorageConsumer();
                await context.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
                Assert.Equal([1, 2, 3], consumer.Bytes);
            }
        }
    }

    [Fact]
    public async Task List_KeyPrefixWithPatternCharacters_RoundTrips()
    {
        TestUtils.CheckForRedis();
        var keyPrefix = $"orleans-tests/journaling/literal*?[x]\\{Guid.NewGuid():N}";
        await using var context = await RedisJournalStorageTestContext.CreateAsync(
            TestContext.Current.CancellationToken,
            options => options.KeyPrefix = keyPrefix);
        var id = JournalId.Create("redis", "pattern-prefix");
        await context.Provider.CreateStorage(id).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            [id],
            await ToListAsync(
                context.Provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MappedKeyCollision_IsRejected()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(
            TestContext.Current.CancellationToken,
            options => options.GetKeyName = _ => "shared");
        var first = context.Provider.CreateStorage(JournalId.Create("redis", "collision", "first"));
        var second = context.Provider.CreateStorage(JournalId.Create("redis", "collision", "second"));
        Assert.True(await first.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => second.CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("key mapping collision", exception.Message);
    }

    [Fact]
    public async Task Replace_ResetsCompactionAccounting()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(
            TestContext.Current.CancellationToken,
            options => options.CompactionThresholdBytes = 3);
        var storage = context.Provider.CreateStorage(JournalId.Create("redis", "compaction"));

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([1, 2, 3, 4]), TestContext.Current.CancellationToken);
        Assert.False(storage.IsCompactionRequested);
        await storage.AppendAsync(new ReadOnlySequence<byte>([5, 6]), TestContext.Current.CancellationToken);
        Assert.False(storage.IsCompactionRequested);
        await storage.AppendAsync(new ReadOnlySequence<byte>([7]), TestContext.Current.CancellationToken);
        Assert.True(storage.IsCompactionRequested);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    public async Task InvalidAppendLength_BlocksMutations(string invalidAppendLength)
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var id = JournalId.Create("redis", "invalid-append-length", invalidAppendLength);
        var storage = context.Provider.CreateStorage(id);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var metadata = await storage.GetMetadataAsync(TestContext.Current.CancellationToken);
        var metadataKey = context.GetMetadataKey(id);
        await context.Database.HashSetAsync(metadataKey, RedisJournalStorage.AppendLengthMetadataKey, invalidAppendLength);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ReplaceAsync(new ReadOnlySequence<byte>([3]), TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.UpdateMetadataAsync(
                new Dictionary<string, string> { ["owner"] = "invalid" },
                expectedETag: metadata!.ETag,
                cancellationToken: TestContext.Current.CancellationToken).AsTask());

        await context.Database.HashSetAsync(metadataKey, RedisJournalStorage.AppendLengthMetadataKey, "1");
        var consumer = new CapturingJournalStorageConsumer();
        await context.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([1], consumer.Bytes);
        Assert.False((await storage.GetMetadataAsync(TestContext.Current.CancellationToken))!.Properties.ContainsKey("owner"));
    }

    [Fact]
    public async Task CurrentSchemaVersion_IsStored()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var id = JournalId.Create("redis", "schema-version");
        await context.Provider.CreateStorage(id).AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);

        var schemaVersion = await context.Database.HashGetAsync(
            context.GetMetadataKey(id),
            RedisJournalStorage.SchemaVersionMetadataKey);

        Assert.Equal(RedisJournalStorage.CurrentSchemaVersion, schemaVersion.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2")]
    public async Task MissingOrUnsupportedSchemaVersion_BlocksAccessAndMutations(string? schemaVersion)
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var id = JournalId.Create("redis", "unsupported-schema", schemaVersion ?? "missing");
        var storage = context.Provider.CreateStorage(id);
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), TestContext.Current.CancellationToken);
        var metadata = await storage.GetMetadataAsync(TestContext.Current.CancellationToken);
        var metadataKey = context.GetMetadataKey(id);
        if (schemaVersion is null)
        {
            await context.Database.HashDeleteAsync(metadataKey, RedisJournalStorage.SchemaVersionMetadataKey);
        }
        else
        {
            await context.Database.HashSetAsync(metadataKey, RedisJournalStorage.SchemaVersionMetadataKey, schemaVersion);
        }

        await Assert.ThrowsAsync<NotSupportedException>(
            () => context.Provider.CreateStorage(id).GetMetadataAsync(TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => context.Provider.CreateStorage(id).ReadAsync(
                new CapturingJournalStorageConsumer(),
                TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([2]), TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => storage.ReplaceAsync(new ReadOnlySequence<byte>([3]), TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => storage.UpdateMetadataAsync(
                new Dictionary<string, string> { ["owner"] = "unsupported" },
                expectedETag: metadata!.ETag,
                cancellationToken: TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => storage.DeleteAsync(TestContext.Current.CancellationToken).AsTask());

        await context.Database.HashSetAsync(
            metadataKey,
            RedisJournalStorage.SchemaVersionMetadataKey,
            RedisJournalStorage.CurrentSchemaVersion);
        var consumer = new CapturingJournalStorageConsumer();
        await context.Provider.CreateStorage(id).ReadAsync(consumer, TestContext.Current.CancellationToken);
        Assert.Equal([1], consumer.Bytes);
        Assert.False((await storage.GetMetadataAsync(TestContext.Current.CancellationToken))!.Properties.ContainsKey("owner"));
    }

    [Fact]
    public async Task CallerMayUseFormatMetadata()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var storage = context.Provider.CreateStorage(JournalId.Create("redis", "caller-format"));
        await storage.CreateIfNotExistsAsync(
            new Dictionary<string, string> { ["format"] = "caller" },
            TestContext.Current.CancellationToken);

        var metadata = await storage.GetMetadataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new JournaledStateManagerOptions().JournalFormatKey, metadata!.Format);
        Assert.Equal("caller", metadata.Properties["format"]);
    }

    [Fact]
    public async Task CallerCannotSetProviderOwnedProperties()
    {
        TestUtils.CheckForRedis();
        await using var context = await RedisJournalStorageTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var provider = context.Provider;
        var storage = provider.CreateStorage(JournalId.Create("redis", "reserved"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.CreateIfNotExistsAsync(
                new Dictionary<string, string> { ["$owner"] = "provider" },
                TestContext.Current.CancellationToken).AsTask());

        await Assert.ThrowsAsync<ArgumentException>(
            () => storage.UpdateMetadataAsync(
                new Dictionary<string, string> { ["$format"] = "provider" },
                cancellationToken: TestContext.Current.CancellationToken).AsTask());
    }

    private static async Task<bool> TryAppendAsync(
        IJournalStorage storage,
        byte[] value,
        CancellationToken cancellationToken)
    {
        try
        {
            await storage.AppendAsync(new ReadOnlySequence<byte>(value), cancellationToken);
            return true;
        }
        catch (InconsistentStateException)
        {
            return false;
        }
    }

    private static ReadOnlySequence<byte> CreateSequence(byte[] first, byte[] second)
    {
        var firstSegment = new ByteSequenceSegment(first);
        var lastSegment = firstSegment.Append(second);
        return new(firstSegment, 0, lastSegment, lastSegment.Memory.Length);
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

        public IDatabase Database => _multiplexer.GetDatabase();

        public RedisKey GetMetadataKey(JournalId journalId)
            => RedisJournalStorage.GetMetadataKey(_keyPrefix, journalId.Value);

        public static async Task<RedisJournalStorageTestContext> CreateAsync(
            CancellationToken cancellationToken,
            Action<RedisJournalStorageOptions>? configure = null)
        {
            var keyPrefix = $"orleans-tests/journaling/{Guid.NewGuid():N}";
            var connectionString = TestDefaultConfiguration.RedisConnectionString
                ?? throw new InvalidOperationException("Redis connection string is not configured.");
            var connectTask = ConnectionMultiplexer.ConnectAsync(connectionString);
            ConnectionMultiplexer multiplexer;
            try
            {
                multiplexer = await connectTask.WaitAsync(cancellationToken);
            }
            catch
            {
                _ = connectTask.ContinueWith(
                    static task =>
                    {
                        if (task.IsCompletedSuccessfully)
                        {
                            task.Result.Dispose();
                        }
                        else
                        {
                            _ = task.Exception;
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }

            var options = new RedisJournalStorageOptions
            {
                ConfigurationOptions = ConfigurationOptions.Parse(connectionString),
                CreateMultiplexer = _ => Task.FromResult(((IConnectionMultiplexer)multiplexer, true)),
                KeyPrefix = keyPrefix,
                ReadChunkSize = 2,
            };
            configure?.Invoke(options);
            keyPrefix = options.KeyPrefix!;

            var provider = new RedisJournalStorageProvider(
                Options.Create(options),
                Options.Create(new ClusterOptions { ServiceId = $"redis-journaling-tests-{Guid.NewGuid():N}" }),
                Options.Create(new JournaledStateManagerOptions()));

            var lifecycle = new SiloLifecycleSubject(Microsoft.Extensions.Logging.Abstractions.NullLogger<SiloLifecycleSubject>.Instance);
            provider.Participate(lifecycle);
            try
            {
                await lifecycle.OnStart(cancellationToken);
                return new(provider, keyPrefix, multiplexer);
            }
            catch
            {
                multiplexer.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            var database = _multiplexer.GetDatabase();
            foreach (var server in _multiplexer.GetServers())
            {
                await foreach (var key in server.KeysAsync(pattern: "*"))
                {
                    if (key.ToString().StartsWith($"{_keyPrefix}:", StringComparison.Ordinal))
                    {
                        await database.KeyDeleteAsync(key);
                    }
                }
            }

            await _multiplexer.CloseAsync();
            _multiplexer.Dispose();
        }
    }

    private sealed class FixedRecordJournalStorageConsumer(int recordSize) : IJournalStorageConsumer
    {
        public List<byte[]> Records { get; } = [];

        public bool IsCompleted { get; private set; }

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            while (buffer.Length >= recordSize)
            {
                var record = new byte[recordSize];
                buffer.Read(record);
                Records.Add(record);
            }

            if (buffer.IsCompleted)
            {
                Assert.Equal(0, buffer.Length);
                IsCompleted = true;
            }
        }
    }

    private sealed class ByteSequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public ByteSequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public ByteSequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new ByteSequenceSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length,
            };
            Next = next;
            return next;
        }
    }

    private sealed class CancellingJournalStorageConsumer(CancellationTokenSource cancellation) : IJournalStorageConsumer
    {
        public bool IsCompleted { get; private set; }

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            buffer.Skip(buffer.Length);
            if (buffer.IsCompleted)
            {
                IsCompleted = true;
            }
            else
            {
                cancellation.Cancel();
            }
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
