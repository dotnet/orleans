using System.Net;
using Orleans.Journaling.Ranges;
using StackExchange.Redis;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Tests how silos initialize and publish the partitions they host.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class GarnetJournalPartitionManagerTests
{
    private static readonly PartitionId P0 = new("p0");

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public void LocalEndPoint_ReplacesWildcardAddressesWithLoopback()
    {
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 6380), GarnetJournalPartitionManager.GetLocalEndPoint(new IPEndPoint(IPAddress.Any, 6380)));
        Assert.Equal(new IPEndPoint(IPAddress.IPv6Loopback, 6380), GarnetJournalPartitionManager.GetLocalEndPoint(new IPEndPoint(IPAddress.IPv6Any, 6380)));
        var specific = new IPEndPoint(IPAddress.Parse("10.1.2.3"), 6380);
        Assert.Same(specific, GarnetJournalPartitionManager.GetLocalEndPoint(specific));
    }

    [Fact]
    public async Task WildcardListenEndPoint_InitializesAndPublishesTheHostName()
    {
        var directory = GarnetPartitionFixture.CreateTemporaryDirectory();
        var store = new InMemoryRangeMetaStore();
        var provider = GarnetPartitionFixture.CreateProvider(directory, store, options => options.ListenEndPoint = new IPEndPoint(IPAddress.Any, 0));
        try
        {
            await provider.StartAsync(CancellationToken);
            var host = Assert.Single(provider.HostedPartitions).Value;
            Assert.Equal(IPAddress.Any, host.EndPoint.Address);
            var published = (await store.ReadAsync(CancellationToken))!.Map.Partitions[P0].Endpoint;
            Assert.Equal($"{Dns.GetHostName()}:{host.EndPoint.Port}", published);

            await using var connection = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
            {
                EndPoints = { GarnetJournalPartitionManager.GetLocalEndPoint(host.EndPoint) },
                Protocol = RedisProtocol.Resp2,
            });
            Assert.Equal("1", (string?)await connection.GetDatabase().HashGetAsync(GarnetJournalKeys.PartitionKey, GarnetJournalKeys.InitializedField));
        }
        finally
        {
            await provider.StopAsync(CancellationToken);
            GarnetPartitionFixture.TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task InterruptedInitialization_ResumesOnlyForTheSamePartitionAndMap()
    {
        var directory = GarnetPartitionFixture.CreateTemporaryDirectory();
        try
        {
            using var host = GarnetJournalPartitionHost.Start(
                new GarnetJournalPartitionSettings { DataDirectory = directory, LogMemorySize = "64m", IndexMemorySize = "1m" },
                loggerFactory: null);
            await using var connection = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions { EndPoints = { host.EndPoint }, Protocol = RedisProtocol.Resp2 });
            var database = connection.GetDatabase();
            var map = RangeMap.CreateUniform(16, [new(P0, new PartitionHosting("host", 0, "p0"))]);

            // An initialization which claimed its identity and opened one bucket before crashing.
            await database.HashSetAsync(
                GarnetJournalKeys.PartitionKey,
                [
                    new(GarnetJournalKeys.IdentityField, GarnetJournalPartitionManager.FormatIdentity(map.Id, P0.Value, map.BucketCount.ToString())),
                    new(GarnetJournalKeys.MapIdField, map.Id),
                    new(GarnetJournalKeys.PartitionIdField, P0.Value),
                    new(GarnetJournalKeys.BucketCountField, map.BucketCount),
                    new(GarnetJournalKeys.SchemaVersionField, GarnetJournalProtocol.CurrentSchemaVersion),
                ]);
            await database.ExecuteAsync(GarnetJournalProtocol.Bucket, [(RedisKey)GarnetJournalKeys.GetBucketKey(map.Id, 0), GarnetJournalProtocol.BucketOpenOperation, "0", GarnetJournalProtocol.NoSource, "1"]);

            // The storage belongs to p0 of this map: another map, or another partition, cannot take it over.
            var otherMap = map with { Id = RangeMap.CreateId() };
            await Assert.ThrowsAsync<InvalidOperationException>(() => GarnetJournalPartitionManager.InitializeAsync(host.EndPoint, P0, otherMap, CancellationToken));
            var p1 = new PartitionId("p1");
            var otherPartition = RangeMap.CreateUniform(16, [new(p1, new PartitionHosting("host", 0, "p1"))]) with { Id = map.Id };
            await Assert.ThrowsAsync<InvalidOperationException>(() => GarnetJournalPartitionManager.InitializeAsync(host.EndPoint, p1, otherPartition, CancellationToken));
            for (var bucket = 0; bucket < map.BucketCount; bucket++)
            {
                Assert.False(await database.KeyExistsAsync(GarnetJournalKeys.GetBucketKey(otherMap.Id, bucket)));
            }

            Assert.False(await database.HashExistsAsync(GarnetJournalKeys.PartitionKey, GarnetJournalKeys.InitializedField));

            // The same partition and map resume the initialization.
            await GarnetJournalPartitionManager.InitializeAsync(host.EndPoint, P0, map, CancellationToken);
            for (var bucket = 0; bucket < map.BucketCount; bucket++)
            {
                Assert.Equal("open", (string?)await database.HashGetAsync(GarnetJournalKeys.GetBucketKey(map.Id, bucket), "state"));
            }

            Assert.Equal("1", (string?)await database.HashGetAsync(GarnetJournalKeys.PartitionKey, GarnetJournalKeys.InitializedField));
            await Assert.ThrowsAsync<InvalidOperationException>(() => GarnetJournalPartitionManager.InitializeAsync(host.EndPoint, P0, otherMap, CancellationToken));
        }
        finally
        {
            GarnetPartitionFixture.TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ConcurrentInitialization_WithConflictingIdentities_StartsOnlyOne()
    {
        var directory = GarnetPartitionFixture.CreateTemporaryDirectory();
        try
        {
            using var host = GarnetJournalPartitionHost.Start(
                new GarnetJournalPartitionSettings { DataDirectory = directory, LogMemorySize = "64m", IndexMemorySize = "1m" },
                loggerFactory: null);
            await using var connection = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions { EndPoints = { host.EndPoint }, Protocol = RedisProtocol.Resp2 });
            var database = connection.GetDatabase();

            // Maps which differ in identity and bucket count race to initialize the same storage.
            var maps = new[] { 16, 32 }.Select(count => RangeMap.CreateUniform(count, [new(P0, new PartitionHosting("host", 0, "p0"))])).ToArray();
            var failures = await Task.WhenAll(maps.Select(map => Capture(GarnetJournalPartitionManager.InitializeAsync(host.EndPoint, P0, map, CancellationToken))));

            Assert.Single(failures, failure => failure is null);
            Assert.All(failures.Where(failure => failure is not null), failure => Assert.IsType<InvalidOperationException>(failure));
            var winner = maps[Array.IndexOf(failures, null)];
            var loser = maps[1 - Array.IndexOf(failures, null)];
            Assert.Equal(winner.Id, (string?)await database.HashGetAsync(GarnetJournalKeys.PartitionKey, GarnetJournalKeys.MapIdField));
            Assert.Equal(winner.BucketCount.ToString(), (string?)await database.HashGetAsync(GarnetJournalKeys.PartitionKey, GarnetJournalKeys.BucketCountField));
            Assert.Equal("1", (string?)await database.HashGetAsync(GarnetJournalKeys.PartitionKey, GarnetJournalKeys.InitializedField));

            // The losing initialization opened none of its buckets.
            for (var bucket = 0; bucket < loser.BucketCount; bucket++)
            {
                Assert.False(await database.KeyExistsAsync(GarnetJournalKeys.GetBucketKey(loser.Id, bucket)));
            }
        }
        finally
        {
            GarnetPartitionFixture.TryDeleteDirectory(directory);
        }

        static async Task<Exception?> Capture(Task task)
        {
            try
            {
                await task;
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }
    }

    [Fact]
    public async Task NewPartition_RecordsItsIdentityBeforeOpeningBuckets()
    {
        var directory = GarnetPartitionFixture.CreateTemporaryDirectory();
        try
        {
            using var host = GarnetJournalPartitionHost.Start(
                new GarnetJournalPartitionSettings { DataDirectory = directory, LogMemorySize = "64m", IndexMemorySize = "1m" },
                loggerFactory: null);
            await using var connection = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions { EndPoints = { host.EndPoint }, Protocol = RedisProtocol.Resp2 });
            var database = connection.GetDatabase();
            var map = RangeMap.CreateUniform(16, [new(P0, new PartitionHosting("host", 0, "p0"))]);

            // A bucket which cannot be opened stops the initialization partway through.
            await database.ExecuteAsync(GarnetJournalProtocol.Bucket, [(RedisKey)GarnetJournalKeys.GetBucketKey(map.Id, 5), GarnetJournalProtocol.BucketOpenOperation, "0", GarnetJournalProtocol.NoSource, "1"]);
            await database.ExecuteAsync(GarnetJournalProtocol.Bucket, [(RedisKey)GarnetJournalKeys.GetBucketKey(map.Id, 5), GarnetJournalProtocol.BucketSealOperation, "3", GarnetJournalProtocol.NoSource, "1"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => GarnetJournalPartitionManager.InitializeAsync(host.EndPoint, P0, map, CancellationToken));

            // The identity was claimed before any bucket was opened, and the initialization is not marked complete.
            Assert.Equal(
                GarnetJournalPartitionManager.FormatIdentity(map.Id, P0.Value, map.BucketCount.ToString()),
                (string?)await database.HashGetAsync(GarnetJournalKeys.PartitionKey, GarnetJournalKeys.IdentityField));
            Assert.Equal(map.Id, (string?)await database.HashGetAsync(GarnetJournalKeys.PartitionKey, GarnetJournalKeys.MapIdField));
            Assert.Equal(P0.Value, (string?)await database.HashGetAsync(GarnetJournalKeys.PartitionKey, GarnetJournalKeys.PartitionIdField));
            Assert.False(await database.HashExistsAsync(GarnetJournalKeys.PartitionKey, GarnetJournalKeys.InitializedField));
            await Assert.ThrowsAsync<InvalidOperationException>(() => GarnetJournalPartitionManager.InitializeAsync(host.EndPoint, P0, map with { Id = RangeMap.CreateId() }, CancellationToken));
        }
        finally
        {
            GarnetPartitionFixture.TryDeleteDirectory(directory);
        }
    }
}
