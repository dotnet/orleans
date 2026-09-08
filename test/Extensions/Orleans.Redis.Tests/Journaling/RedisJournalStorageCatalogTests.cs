using System.Net;
using System.Runtime.CompilerServices;
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
    public async Task ListAsync_YieldsFirstBatchBeforeScanCompletesAndSnapshotsPrefix()
    {
        var firstId = JournalId.Create("redis", "list", "z");
        var secondId = JournalId.Create("redis", "list", "a");
        var nonmatchingId = JournalId.Create("redis", "listing", "other");
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
        var provider = await CreateProviderAsync(database, CreateServer(ScanAsync(TestContext.Current.CancellationToken)));
        var options = new ListOptions { Prefix = JournalId.Create("redis", "list") };
        await using var enumerator = provider.ListAsync(options, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(firstId, enumerator.Current);
        Assert.Equal(128, scannedKeys);
        Assert.Equal(128, metadataReads);
        Assert.False(scanCompleted);
        Assert.False(scanDisposed);

        options.Prefix = JournalId.Create("redis", "listing");

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

    [Fact]
    public async Task ListAsync_LaterScanFailurePropagatesAfterFirstBatch()
    {
        var id = JournalId.Create("redis", "failure");
        var key = RedisJournalStorage.GetMetadataKey(KeyPrefix, "mapped");
        var failure = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Later scan failed.");
        var scanDisposed = false;
        var scannedKeys = 0;
        var database = Substitute.For<IDatabase>();
        database.HashGetAsync(key, RedisJournalStorage.JournalIdMetadataKey)
            .Returns(Task.FromResult((RedisValue)id.Value));
        var provider = await CreateProviderAsync(database, CreateServer(ScanAsync()));
        await using var enumerator = provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(id, enumerator.Current);
        Assert.Equal(128, scannedKeys);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListAsync_CancellationOrEarlyDisposalStopsScan(bool cancel)
    {
        var id = JournalId.Create("redis", "cancel");
        var key = RedisJournalStorage.GetMetadataKey(KeyPrefix, "mapped");
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
        var provider = await CreateProviderAsync(database, CreateServer(ScanAsync(cancellation.Token)));

        await using (var enumerator = provider.ListAsync(cancellationToken: cancellation.Token).GetAsyncEnumerator(cancellation.Token))
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(id, enumerator.Current);
            Assert.Equal(128, scannedKeys);
            Assert.Equal(128, metadataReads);
            Assert.False(scanDisposed);

            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
            }
        }

        Assert.Equal(128, scannedKeys);
        Assert.Equal(128, metadataReads);
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
        var key = RedisJournalStorage.GetMetadataKey(KeyPrefix, "mapped");
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
        var provider = await CreateProviderAsync(database, CreateServer(ScanAsync()));
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

    private static IServer CreateServer(IAsyncEnumerable<RedisKey> keys)
    {
        var server = Substitute.For<IServer>();
        server.IsConnected.Returns(true);
        server.KeysAsync(0, RedisJournalStorage.GetMetadataKeyPattern(KeyPrefix), pageSize: 250).Returns(keys);
        return server;
    }

    private static async Task<RedisJournalStorageProvider> CreateProviderAsync(IDatabase database, params IServer[] servers)
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetDatabase().Returns(database);
        var endpoints = servers.Select((_, index) => (EndPoint)new DnsEndPoint($"primary-{index}", 6379)).ToArray();
        connection.GetEndPoints().Returns(endpoints);
        for (var i = 0; i < servers.Length; i++)
        {
            connection.GetServer(endpoints[i]).Returns(servers[i]);
        }

        var provider = new RedisJournalStorageProvider(
            Options.Create(new RedisJournalStorageOptions
            {
                KeyPrefix = KeyPrefix,
                CreateMultiplexer = _ => Task.FromResult((connection, true)),
            }),
            Options.Create(new ClusterOptions { ServiceId = "catalog-tests" }),
            Options.Create(new JournaledStateManagerOptions()));
        var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
        provider.Participate(lifecycle);
        await lifecycle.OnStart(TestContext.Current.CancellationToken);
        return provider;
    }
}
