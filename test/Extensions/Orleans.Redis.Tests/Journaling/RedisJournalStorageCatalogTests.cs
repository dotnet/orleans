using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Journaling;
using Orleans.Runtime;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Journaling;

[TestSuite("BVT")]
[TestProvider("Redis")]
[TestArea("Journaling")]
[TestCategory("BVT")]
public sealed class RedisJournalStorageCatalogTests
{
    private const string KeyPrefix = "catalog-tests";

    [Fact]
    public async Task ListAsync_CustomMappingYieldsFirstBatchBeforeScanCompletesAndSnapshotsOptions()
    {
        var firstId = JournalId.Create("redis", "list", "z");
        var secondId = JournalId.Create("redis", "list", "a");
        var nonmatchingId = JournalId.Create("redis", "other");
        var firstBatch = Enumerable.Range(0, 128)
            .Select(index => RedisJournalStorage.GetMetadataKey(KeyPrefix, $"mapped-{index}"))
            .ToArray();
        var secondKey = RedisJournalStorage.GetMetadataKey(KeyPrefix, "mapped-second");
        var ids = firstBatch.ToDictionary(key => key, _ => firstId);
        ids[firstBatch[^1]] = nonmatchingId;
        ids[secondKey] = secondId;
        var scannedKeys = 0;
        var metadataReads = 0;
        var scanCompleted = false;
        var scanDisposed = false;
        var database = Substitute.For<IDatabase>();
        database.HashGetAsync(Arg.Any<RedisKey>(), RedisJournalStorage.JournalIdMetadataKey)
            .Returns(call =>
            {
                metadataReads++;
                return Task.FromResult((RedisValue)ids[call.ArgAt<RedisKey>(0)].Value);
            });
        var provider = await CreateProviderAsync(database, CustomMappingOptions(), CreateServer(ScanAsync(TestContext.Current.CancellationToken)));
        var options = new ListOptions { Prefix = nonmatchingId, MinId = firstId, MaxId = new("a") };
        var listing = provider.ListAsync(options, TestContext.Current.CancellationToken);
        options.Prefix = JournalId.Create("redis", "list");
        options.MinId = secondId;
        options.MaxId = firstId;
        await using var enumerator = listing.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(firstId, enumerator.Current);
        Assert.Equal(128, scannedKeys);
        Assert.Equal(128, metadataReads);
        Assert.False(scanCompleted);
        Assert.False(scanDisposed);

        options.Prefix = JournalId.Create("redis", "listing");
        options.MinId = firstId;
        options.MaxId = new("a");

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(secondId, enumerator.Current);
        Assert.Equal(130, scannedKeys);
        Assert.Equal(130, metadataReads);
        Assert.True(scanCompleted);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.True(scanDisposed);

        async IAsyncEnumerable<RedisKey> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.CompletedTask;
                foreach (var key in firstBatch.Concat([firstBatch[0], secondKey]))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scannedKeys++;
                    yield return key;
                }

                scanCompleted = true;
            }
            finally
            {
                scanDisposed = true;
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ListAsync_BoundsAreInclusiveAndDoNotStopUnorderedScan(bool bounded, bool customMapping)
    {
        var future = JournalId.Create("redis", "list", "z");
        var earlier = JournalId.Create("redis", "list", "a");
        var maximum = JournalId.Create("redis", "list", "b");
        var before = JournalId.Create("redis", "list", "0");
        var partialMatch = JournalId.Create("redis", "listing");
        var ids = Enumerable.Repeat(future, 128).Concat([earlier, maximum, before, partialMatch, JournalId.Create("redis", "other")]).ToArray();
        var keys = ids.Select((id, index) => RedisJournalStorage.GetMetadataKey(KeyPrefix, customMapping ? $"mapped-{index}" : id.Value)).ToArray();
        var database = Substitute.For<IDatabase>();
        for (var index = 0; index < keys.Length; index++)
        {
            database.HashGetAsync(keys[index], RedisJournalStorage.JournalIdMetadataKey)
                .Returns(Task.FromResult((RedisValue)ids[index].Value));
        }

        var scanned = 0;
        var provider = await CreateProviderAsync(
            database,
            customMapping ? CustomMappingOptions() : new(),
            CreateServer(ScanAsync(), customMapping ? null : bounded ? "redis/list/" : "redis/list"));
        var result = new List<JournalId>();
        await foreach (var id in provider.ListAsync(
            new() { Prefix = JournalId.Create("redis", "list"), MinId = bounded ? earlier : default, MaxId = bounded ? maximum : default },
            TestContext.Current.CancellationToken))
        {
            result.Add(id);
        }

        Assert.Equal(bounded ? [earlier, maximum] : new[] { future, earlier, maximum, before, partialMatch }, result);
        Assert.Equal(133, scanned);
        if (!customMapping)
        {
            await AssertNoMetadataReadsAsync(database);
        }

        async IAsyncEnumerable<RedisKey> ScanAsync()
        {
            await Task.CompletedTask;
            foreach (var key in keys)
            {
                scanned++;
                yield return key;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAsync_LaterScanFailurePropagatesAfterEarlierIds(bool customMapping)
    {
        var id = JournalId.Create("redis", "failure");
        var key = RedisJournalStorage.GetMetadataKey(KeyPrefix, customMapping ? "mapped" : id.Value);
        var failure = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Later scan failed.");
        var scanDisposed = false;
        var scannedKeys = 0;
        var database = Substitute.For<IDatabase>();
        database.HashGetAsync(key, RedisJournalStorage.JournalIdMetadataKey)
            .Returns(Task.FromResult((RedisValue)id.Value));
        var provider = await CreateProviderAsync(database, customMapping ? CustomMappingOptions() : new(), CreateServer(ScanAsync()));
        await using var enumerator = provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(id, enumerator.Current);
        Assert.Equal(customMapping ? 128 : 1, scannedKeys);
        Assert.False(scanDisposed);

        var exception = await Assert.ThrowsAsync<RedisConnectionException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Same(failure, exception);
        Assert.Equal(128, scannedKeys);
        Assert.True(scanDisposed);

        async IAsyncEnumerable<RedisKey> ScanAsync()
        {
            try
            {
                await Task.CompletedTask;
                for (var i = 0; i < 128; i++)
                {
                    scannedKeys++;
                    yield return key;
                }

                throw failure;
            }
            finally
            {
                scanDisposed = true;
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ListAsync_CancellationOrEarlyDisposalStopsScan(bool cancel, bool customMapping)
    {
        var id = JournalId.Create("redis", "cancel");
        var key = RedisJournalStorage.GetMetadataKey(KeyPrefix, customMapping ? "mapped" : id.Value);
        var scannedKeys = 0;
        var metadataReads = 0;
        var scanDisposed = false;
        var database = Substitute.For<IDatabase>();
        database.HashGetAsync(key, RedisJournalStorage.JournalIdMetadataKey)
            .Returns(_ =>
            {
                metadataReads++;
                return Task.FromResult((RedisValue)id.Value);
            });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var provider = await CreateProviderAsync(database, customMapping ? CustomMappingOptions() : new(), CreateServer(ScanAsync(cancellation.Token)));

        await using (var enumerator = provider.ListAsync(cancellationToken: cancellation.Token).GetAsyncEnumerator(cancellation.Token))
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(id, enumerator.Current);
            Assert.Equal(customMapping ? 128 : 1, scannedKeys);
            Assert.Equal(customMapping ? 128 : 0, metadataReads);
            Assert.False(scanDisposed);

            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
            }
        }

        Assert.Equal(customMapping ? 128 : 1, scannedKeys);
        Assert.Equal(customMapping ? 128 : 0, metadataReads);
        Assert.True(scanDisposed);

        async IAsyncEnumerable<RedisKey> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.CompletedTask;
                for (var i = 0; i < 129; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scannedKeys++;
                    yield return key;
                }
            }
            finally
            {
                scanDisposed = true;
            }
        }
    }

    [Fact]
    public async Task ListAsync_DisconnectedLaterPrimaryPropagatesAfterEarlierIds()
    {
        var id = JournalId.Create("redis", "earlier-server");
        var key = RedisJournalStorage.GetMetadataKey(KeyPrefix, id.Value);
        var database = Substitute.For<IDatabase>();
        database.HashGetAsync(key, RedisJournalStorage.JournalIdMetadataKey)
            .Returns(Task.FromResult((RedisValue)id.Value));
        var disconnected = Substitute.For<IServer>();
        disconnected.IsConnected.Returns(false);
        var provider = await CreateProviderAsync(database, CreateServer(ScanAsync()), disconnected);
        await using var enumerator = provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(id, enumerator.Current);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Contains("not connected", exception.Message);

        async IAsyncEnumerable<RedisKey> ScanAsync()
        {
            await Task.CompletedTask;
            yield return key;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAsync_NoPrimaryThrows(bool hasReplica)
    {
        var replica = Substitute.For<IServer>();
        replica.IsReplica.Returns(true);
        replica.IsConnected.Returns(true);
        var provider = await CreateProviderAsync(Substitute.For<IDatabase>(), hasReplica ? [replica] : []);
        await using var enumerator = provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal("No connected primary Redis servers are available for journal discovery.", exception.Message);
    }

    [Fact]
    public async Task ListAsync_CancellationDuringFinalDeletedMetadataResponsePropagates()
    {
        var key = RedisJournalStorage.GetMetadataKey(KeyPrefix, "deleted");
        var database = Substitute.For<IDatabase>();
        database.HashGetAsync(key, RedisJournalStorage.JournalIdMetadataKey)
            .Returns(Task.FromResult(RedisValue.Null));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var scriptCalls = 0;
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(_ =>
            {
                scriptCalls++;
                cancellation.Cancel();
                return Task.FromResult(RedisResult.Create(new RedisValue[] { 0 }));
            });
        var provider = await CreateProviderAsync(database, CustomMappingOptions(), CreateServer(ScanAsync()));
        await using var enumerator = provider.ListAsync(cancellationToken: cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, scriptCalls);

        async IAsyncEnumerable<RedisKey> ScanAsync()
        {
            await Task.CompletedTask;
            yield return key;
        }
    }

    [Fact]
    public async Task ListAsync_CancellationDuringFinalScanCompletionPropagates()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var provider = await CreateProviderAsync(Substitute.For<IDatabase>(), CreateServer(ScanAsync()));
        await using var enumerator = provider.ListAsync(cancellationToken: cancellation.Token).GetAsyncEnumerator(cancellation.Token);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);

        async IAsyncEnumerable<RedisKey> ScanAsync()
        {
            await Task.CompletedTask;
            cancellation.Cancel();
            yield break;
        }
    }

    [Fact]
    public async Task ListAsync_DefaultMappingSnapshotsOptionsAndStreamsWithoutMetadataReads()
    {
        var first = new JournalId("jobs/2026/09/b");
        var second = new JournalId("jobs/2026/09/a");
        var unrelated = new JournalId("jobs/other");
        var scanned = 0;
        var database = Substitute.For<IDatabase>();
        var server = CreateServer(ScanAsync(), "jobs/2026/09/");
        var provider = await CreateProviderAsync(database, server);
        var options = new ListOptions { Prefix = unrelated, MinId = first, MaxId = new("a") };
        var listing = provider.ListAsync(options, TestContext.Current.CancellationToken);
        options.Prefix = new("jobs/2026/0");
        options.MinId = second;
        options.MaxId = first;
        await using var enumerator = listing.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(first, enumerator.Current);
        Assert.Equal(1, scanned);
        await AssertNoMetadataReadsAsync(database);

        options.Prefix = unrelated;
        options.MinId = first;
        options.MaxId = new("a");

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(second, enumerator.Current);
        Assert.Equal(4, scanned);
        Assert.False(await enumerator.MoveNextAsync());
        _ = server.Received(1).KeysAsync(0, "catalog-tests:journal:{*}:jobs%2F2026%2F09%2F*:metadata", pageSize: 250);
        await AssertNoMetadataReadsAsync(database);

        async IAsyncEnumerable<RedisKey> ScanAsync()
        {
            await Task.CompletedTask;
            foreach (var id in new[] { first, first, unrelated, second })
            {
                scanned++;
                yield return RedisJournalStorage.GetMetadataKey(KeyPrefix, id.Value);
            }
        }
    }

    [Fact]
    public async Task ListAsync_DefaultMappingReadsAllIdsFromKeysWithoutMetadataRequests()
    {
        var ids = new JournalId[] { new("redis/raw:*?[\\]/%2F:雪/😀"), new("redis/other") };
        var database = Substitute.For<IDatabase>();
        var provider = await CreateProviderAsync(
            database,
            CreateServer(ScanKeysAsync(ids.Concat(ids).Select(id => RedisJournalStorage.GetMetadataKey(KeyPrefix, id.Value)))));

        var result = await ReadIdsAsync(provider);

        Assert.Equal(ids, result);
        await AssertNoMetadataReadsAsync(database);
    }

    [Fact]
    public async Task ListAsync_DefaultMappingEscapesNativePatternAndMatchesPartialSegments()
    {
        const string keyPrefix = @"catalog[*?]\tests";
        const string prefix = @"jobs/*?[x]\%/雪";
        const string expectedPattern = @"catalog\[\*\?\]\\tests:journal:{*}:jobs%2F%2A%3F%5Bx%5D%5C%25%2F%E9%9B%AA*:metadata";
        var expected = new JournalId[] { new(prefix), new(prefix + "flake/child") };
        var unrelated = new JournalId("jobs/wildcard-not-a-literal");
        var keys = expected.Append(unrelated).Select(id => RedisJournalStorage.GetMetadataKey(keyPrefix, id.Value));
        var database = Substitute.For<IDatabase>();
        var server = CreateServer(ScanKeysAsync(keys), prefix, keyPrefix);
        var provider = await CreateProviderAsync(database, new RedisJournalStorageOptions { KeyPrefix = keyPrefix }, server);

        var result = await ReadIdsAsync(provider, new() { Prefix = new(prefix) });

        Assert.Equal(expected, result);
        Assert.Equal((RedisValue)expectedPattern, RedisJournalStorage.GetMetadataKeyPattern(keyPrefix, prefix));
        _ = server.Received(1).KeysAsync(0, expectedPattern, pageSize: 250);
        await AssertNoMetadataReadsAsync(database);
    }

    [Theory]
    [InlineData("jobs/", "jobs/123/a", "jobs/123/z", "jobs%2F123%2F")]
    [InlineData(null, "jobs/123/a", "jobs/123/z", "jobs%2F123%2F")]
    [InlineData("jobs/123/alpha", "jobs/123/a", "jobs/123/z", "jobs%2F123%2Falpha")]
    [InlineData(null, "jobs/a", "other/z", "")]
    [InlineData(null, "jobs/a", null, "")]
    [InlineData(null, null, "jobs/z", "")]
    public async Task ListAsync_DefaultMappingNarrowsNativePatternUsingCommonBounds(
        string? prefix, string? minId, string? maxId, string expectedEncodedPrefix)
    {
        var database = Substitute.For<IDatabase>();
        var server = Substitute.For<IServer>();
        server.IsConnected.Returns(true);
        server.KeysAsync(0, Arg.Any<RedisValue>(), pageSize: 250).Returns(ScanKeysAsync([]));
        var provider = await CreateProviderAsync(database, server);

        var result = await ReadIdsAsync(provider, new()
        {
            Prefix = prefix is null ? default : new(prefix),
            MinId = minId is null ? default : new(minId),
            MaxId = maxId is null ? default : new(maxId),
        });

        Assert.Empty(result);
        _ = server.Received(1).KeysAsync(0, $"catalog-tests:journal:{{*}}:{expectedEncodedPrefix}*:metadata", pageSize: 250);
        await AssertNoMetadataReadsAsync(database);
    }

    [Fact]
    public async Task ListAsync_DefaultMappingBroadensNativePrefixEndingInHighSurrogate()
    {
        var included = new JournalId("jobs/\U0001F600");
        var excluded = new JournalId("jobs/\u96EA");
        var database = Substitute.For<IDatabase>();
        var server = CreateServer(
            ScanKeysAsync(new[] { included, excluded }.Select(id => RedisJournalStorage.GetMetadataKey(KeyPrefix, id.Value))),
            "jobs/");
        var provider = await CreateProviderAsync(database, server);

        var result = await ReadIdsAsync(provider, new() { Prefix = new("jobs/\uD83D") });

        Assert.Equal([included], result);
        _ = server.Received(1).KeysAsync(0, "catalog-tests:journal:{*}:jobs%2F*:metadata", pageSize: 250);
        await AssertNoMetadataReadsAsync(database);
    }

    [Theory]
    [InlineData("jobs/", "z", "a", false)]
    [InlineData("jobs/", "z", "a", true)]
    [InlineData("jobs/", "other", null, false)]
    [InlineData("jobs/", "other", null, true)]
    [InlineData("jobs/", null, "a", false)]
    [InlineData("jobs/", null, "a", true)]
    public async Task ListAsync_EmptyRangePerformsNoScanOrReads(string prefix, string? minId, string? maxId, bool customMapping)
    {
        var database = Substitute.For<IDatabase>();
        var server = Substitute.For<IServer>();
        server.IsConnected.Returns(true);
        var provider = await CreateProviderAsync(database, customMapping ? CustomMappingOptions() : new(), server);
        database.ClearReceivedCalls();
        server.ClearReceivedCalls();

        var result = await ReadIdsAsync(provider, new()
        {
            Prefix = new(prefix),
            MinId = minId is null ? default : new(minId),
            MaxId = maxId is null ? default : new(maxId),
        });

        Assert.Empty(result);
        Assert.Empty(database.ReceivedCalls());
        Assert.Empty(server.ReceivedCalls());
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad%")]
    [InlineData("journal%2fchild")]
    [InlineData("journal%2Fother")]
    [InlineData("journal/child")]
    [InlineData("%FF")]
    [InlineData("%20")]
    public async Task ListAsync_DefaultMappingRejectsMalformedMetadataKeysWithoutFallback(string encodedName)
    {
        var metadataKey = RedisJournalStorage.GetMetadataKey(KeyPrefix, "journal/child").ToString();
        metadataKey = metadataKey[..(metadataKey.IndexOf("}:", StringComparison.Ordinal) + 2)] + encodedName + ":metadata";
        var database = Substitute.For<IDatabase>();
        var provider = await CreateProviderAsync(database, CreateServer(ScanKeysAsync([(RedisKey)metadataKey])));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadIdsAsync(provider));

        Assert.Contains("malformed", exception.Message);
        Assert.Contains(metadataKey, exception.Message);
        await AssertNoMetadataReadsAsync(database);
    }

    [Fact]
    public void MetadataKeyPatternRequiresReadableComponentAndKeysPreserveHashTag()
    {
        const string keyName = "jobs/raw:*?[\\]/%2F:雪/😀";
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyName)));
        const string encodedName = "jobs%2Fraw%3A%2A%3F%5B%5C%5D%2F%252F%3A%E9%9B%AA%2F%F0%9F%98%80";
        var metadataKey = RedisJournalStorage.GetMetadataKey(KeyPrefix, keyName);
        var dataKey = RedisJournalStorage.GetDataKey(KeyPrefix, keyName);

        Assert.Equal($"{KeyPrefix}:journal:{{{expectedHash}}}:{encodedName}:metadata", metadataKey.ToString());
        Assert.Equal($"{KeyPrefix}:journal:{{{expectedHash}}}:{encodedName}:data", dataKey.ToString());
        Assert.Equal(new JournalId(keyName), RedisJournalStorage.GetJournalIdFromMetadataKey(KeyPrefix, metadataKey));
        Assert.Equal((RedisValue)"catalog-tests:journal:{*}:*:metadata", RedisJournalStorage.GetMetadataKeyPattern(KeyPrefix));
        var oldKey = (RedisKey)$"{KeyPrefix}:journal:{{{expectedHash}}}:metadata";
        Assert.Throws<InvalidOperationException>(() => RedisJournalStorage.GetJournalIdFromMetadataKey(KeyPrefix, oldKey));
    }

    [Fact]
    public async Task ListAsync_CustomMappingReadsBatchesConcurrently()
    {
        var ids = Enumerable.Range(0, 128).Select(index => new JournalId($"jobs/{index:D3}")).ToArray();
        var keys = ids.Select((_, index) => RedisJournalStorage.GetMetadataKey(KeyPrefix, $"opaque-{index}")).ToArray();
        var completions = ids.Select(_ => new TaskCompletionSource<RedisValue>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var reads = 0;
        var database = Substitute.For<IDatabase>();
        database.HashGetAsync(Arg.Any<RedisKey>(), RedisJournalStorage.JournalIdMetadataKey)
            .Returns(call =>
            {
                var index = Array.IndexOf(keys, call.ArgAt<RedisKey>(0));
                reads++;
                return completions[index].Task;
            });
        var server = CreateServer(ScanKeysAsync(keys));
        var provider = await CreateProviderAsync(database, CustomMappingOptions(), server);
        await using var enumerator = provider.ListAsync(
            new() { Prefix = new("jobs/"), MinId = ids[0], MaxId = ids[^1] },
            TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var moveNext = enumerator.MoveNextAsync().AsTask();
        var startedReads = reads;
        var completedBeforeResponses = moveNext.IsCompleted;

        for (var i = 0; i < completions.Length; i++)
        {
            completions[i].SetResult(ids[i].Value);
        }

        Assert.True(await moveNext);
        Assert.Equal(128, startedReads);
        Assert.False(completedBeforeResponses);
        Assert.Equal(ids[0], enumerator.Current);
        var result = new List<JournalId> { enumerator.Current };
        while (await enumerator.MoveNextAsync())
        {
            result.Add(enumerator.Current);
        }

        Assert.Equal(ids, result);
        _ = server.Received(1).KeysAsync(0, "catalog-tests:journal:{*}:*:metadata", pageSize: 250);
    }

    [Fact]
    public async Task ListAsync_CustomMappingMetadataReadFailurePropagates()
    {
        var key = RedisJournalStorage.GetMetadataKey(KeyPrefix, "opaque");
        var failure = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Metadata read failed.");
        var database = Substitute.For<IDatabase>();
        database.HashGetAsync(key, RedisJournalStorage.JournalIdMetadataKey)
            .Returns(Task.FromException<RedisValue>(failure));
        var provider = await CreateProviderAsync(database, CustomMappingOptions(), CreateServer(ScanKeysAsync([key])));

        var exception = await Assert.ThrowsAsync<RedisConnectionException>(() => ReadIdsAsync(provider));

        Assert.Same(failure, exception);
        await database.Received(1).HashGetAsync(key, RedisJournalStorage.JournalIdMetadataKey);
        await database.DidNotReceive().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>());
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "jobs/recovered")]
    [InlineData(1, " ")]
    [InlineData(-1, null)]
    public async Task ListAsync_CustomMappingMissingMetadataChecksDeletionOrReportsCorruption(int status, string? value)
    {
        var key = RedisJournalStorage.GetMetadataKey(KeyPrefix, "opaque");
        var database = Substitute.For<IDatabase>();
        database.HashGetAsync(key, RedisJournalStorage.JournalIdMetadataKey).Returns(Task.FromResult(RedisValue.Null));
        var response = status == 1 ? new RedisValue[] { status, value } : [status];
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(Task.FromResult(RedisResult.Create(response)));
        var provider = await CreateProviderAsync(database, CustomMappingOptions(), CreateServer(ScanKeysAsync([key])));

        if (status < 0 || string.IsNullOrWhiteSpace(value) && status == 1)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadIdsAsync(provider));
            Assert.Contains(RedisJournalStorage.JournalIdMetadataKey, exception.Message);
        }
        else
        {
            var result = await ReadIdsAsync(provider);
            if (status == 0)
            {
                Assert.Empty(result);
            }
            else
            {
                Assert.NotNull(value);
                Assert.Equal([new JournalId(value)], result);
            }
        }

        await database.Received(1).HashGetAsync(key, RedisJournalStorage.JournalIdMetadataKey);
        await database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Is<RedisKey[]>(keys => keys.Length == 1 && keys[0] == key), Arg.Any<RedisValue[]>());
    }

    private static async IAsyncEnumerable<RedisKey> ScanKeysAsync(IEnumerable<RedisKey> keys)
    {
        await Task.CompletedTask;
        foreach (var key in keys)
        {
            yield return key;
        }
    }

    private static async Task<List<JournalId>> ReadIdsAsync(RedisJournalStorageProvider provider, ListOptions? options = null)
    {
        var result = new List<JournalId>();
        await foreach (var id in provider.ListAsync(options, TestContext.Current.CancellationToken))
        {
            result.Add(id);
        }

        return result;
    }

    private static IServer CreateServer(IAsyncEnumerable<RedisKey> keys, string? journalIdPrefix = null, string keyPrefix = KeyPrefix)
    {
        var server = Substitute.For<IServer>();
        server.IsConnected.Returns(true);
        server.KeysAsync(0, RedisJournalStorage.GetMetadataKeyPattern(keyPrefix, journalIdPrefix), pageSize: 250).Returns(keys);
        return server;
    }

    private static async Task<RedisJournalStorageProvider> CreateProviderAsync(IDatabase database, params IServer[] servers)
        => await CreateProviderAsync(database, new RedisJournalStorageOptions(), servers);

    private static async Task<RedisJournalStorageProvider> CreateProviderAsync(
        IDatabase database,
        RedisJournalStorageOptions options,
        params IServer[] servers)
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase().Returns(database);
        var endpoints = servers.Select((_, index) => (EndPoint)new DnsEndPoint($"primary-{index}", 6379)).ToArray();
        connection.GetEndPoints().Returns(endpoints);
        for (var i = 0; i < servers.Length; i++)
        {
            connection.GetServer(endpoints[i]).Returns(servers[i]);
        }

        options.KeyPrefix ??= KeyPrefix;
        options.CreateMultiplexer = _ => Task.FromResult((connection, true));
        var provider = new RedisJournalStorageProvider(
            Options.Create(options),
            Options.Create(new ClusterOptions { ServiceId = "catalog-tests" }),
            Options.Create(new JournaledStateManagerOptions()));
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        provider.Participate(lifecycle);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);
        return provider;
    }

    private static RedisJournalStorageOptions CustomMappingOptions()
        => new() { GetKeyName = static id => $"opaque-{id.Value}" };

    private static async Task AssertNoMetadataReadsAsync(IDatabase database)
    {
        await database.DidNotReceive().HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>());
        await database.DidNotReceive().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>());
    }
}
