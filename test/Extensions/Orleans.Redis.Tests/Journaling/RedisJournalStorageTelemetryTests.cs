using System.Diagnostics.Metrics;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
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

[TestSuite("BVT"), TestProvider("Redis"), TestArea("Journaling"), TestCategory("BVT")]
public sealed class RedisJournalStorageTelemetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalog_CountsConsumedKeysAndYieldedEntries(bool customMapping)
    {
        using var fixture = await Fixture.CreateAsync(customMapping);
        var ids = new List<JournalId>();
        await foreach (var entry in fixture.Provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            ids.Add(entry.Id);
        }

        Assert.Equal(new[] { new JournalId("tenant/a"), new JournalId("tenant/b") }, ids);
        Assert.Equal(2, fixture.Entries.GetMeasurementSnapshot().Sum(measurement => measurement.Value));
        Assert.All(fixture.Entries.GetMeasurementSnapshot(), entry =>
        {
            Assert.Equal(1, entry.Value);
            Assert.Single(entry.Tags);
            Assert.Equal("redis", entry.Tags["provider"]);
        });
        Assert.Equal(new long[] { 1, 1 }, fixture.Items.GetMeasurementSnapshot().Select(item => item.Value));
        Assert.All(fixture.Items.GetMeasurementSnapshot(), item =>
        {
            Assert.Single(item.Tags);
            Assert.True(item.MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "redis") }));
        });
        Assert.Empty(fixture.Pages.GetMeasurementSnapshot());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Catalog_EarlyStopCountsOnlyConsumedKeysAndYieldedEntries(bool cancel, bool customMapping)
    {
        using var fixture = await Fixture.CreateAsync(customMapping);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using (var enumerator = fixture.Provider.ListAsync(cancellationToken: cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token))
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new JournalId("tenant/a"), enumerator.Current.Id);
            if (cancel)
            {
                cancellation.Cancel();
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
        }

        Assert.Empty(fixture.Pages.GetMeasurementSnapshot());
        Assert.Equal(customMapping ? 2 : 1, fixture.Items.GetMeasurementSnapshot().Sum(item => item.Value));
        Assert.Equal(1, Assert.Single(fixture.Entries.GetMeasurementSnapshot()).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalog_FilteringCountsNativeKeysRatherThanYieldedEntries(bool customMapping)
    {
        using var fixture = await Fixture.CreateAsync(customMapping);
        var ids = new List<JournalId>();
        await foreach (var entry in fixture.Provider.ListAsync(
            new JournalCatalogListOptions { MinId = new("tenant/b") }, TestContext.Current.CancellationToken))
        {
            ids.Add(entry.Id);
        }

        Assert.Equal(new[] { new JournalId("tenant/b") }, ids);
        Assert.Equal(new long[] { 1, 1 }, fixture.Items.GetMeasurementSnapshot().Select(item => item.Value));
        Assert.Equal(1, Assert.Single(fixture.Entries.GetMeasurementSnapshot()).Value);
        Assert.Empty(fixture.Pages.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task Catalog_CustomMappingCountsOnlyScanItemsWhenIdentityNeedsScriptFallback()
    {
        using var fixture = await Fixture.CreateAsync(customMapping: true);
        var key = RedisJournalStorage.GetMetadataKey("metrics", "mapped-tenant/a");
        fixture.Database.HashGetAsync(key, RedisJournalStorage.JournalIdMetadataKey).Returns(RedisValue.Null);
        fixture.Database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(Task.FromResult(RedisResult.Create(new RedisValue[]
                { 1, RedisJournalStorage.EncodeKeyName("tenant/a") })));
        var ids = new List<JournalId>();
        await foreach (var entry in fixture.Provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            ids.Add(entry.Id);
        }

        Assert.Equal(new[] { new JournalId("tenant/a"), new JournalId("tenant/b") }, ids);
        Assert.All(fixture.Items.GetMeasurementSnapshot(), item =>
        {
            Assert.Equal(1, item.Value);
            Assert.Single(item.Tags);
            Assert.Equal("redis", item.Tags["provider"]);
        });
        Assert.Equal(2, fixture.Items.GetMeasurementSnapshot().Sum(item => item.Value));
        Assert.Equal(2, fixture.Entries.GetMeasurementSnapshot().Sum(item => item.Value));
        Assert.Empty(fixture.Pages.GetMeasurementSnapshot());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services;

        private Fixture(bool customMapping)
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            _services = services.BuildServiceProvider();
            var instruments = new OrleansInstruments(_services.GetRequiredService<IMeterFactory>());
            Entries = new(instruments.Meter, "orleans-journaling-provider-catalog-entries");
            Pages = new(instruments.Meter, "orleans-journaling-provider-catalog-pages");
            Items = new(instruments.Meter, "orleans-journaling-provider-catalog-items");
            Connection = Substitute.For<IConnectionMultiplexer>();
            Database = Substitute.For<IDatabase>();
            var server = Substitute.For<IServer>();
            var endpoint = new IPEndPoint(IPAddress.Loopback, 6379);
            Connection.GetDatabase().Returns(Database);
            Connection.GetEndPoints().Returns(new EndPoint[] { endpoint });
            Connection.GetServer(endpoint).Returns(server);
            server.IsConnected.Returns(true);
            var options = new RedisJournalStorageOptions
            {
                KeyPrefix = "metrics",
                CreateMultiplexer = _ => Task.FromResult((Connection, true))
            };
            if (customMapping)
            {
                options.GetKeyName = static id => "mapped-" + id.Value;
            }

            var keys = new List<RedisKey>();
            foreach (var value in new[] { "tenant/a", "tenant/b" })
            {
                var key = RedisJournalStorage.GetMetadataKey("metrics", options.GetKeyName(new(value)));
                keys.Add(key);
                Database.HashGetAsync(key, RedisJournalStorage.JournalIdMetadataKey).Returns((RedisValue)RedisJournalStorage.EncodeKeyName(value));
            }

            server.KeysAsync(0, Arg.Any<RedisValue>(), pageSize: 250).Returns(Keys(keys));
            Provider = new RedisJournalStorageProvider(
                Options.Create(options),
                Options.Create(new ClusterOptions { ServiceId = "metrics" }),
                Options.Create(new JournaledStateManagerOptions()),
                instruments);
            Lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
            Provider.Participate(Lifecycle);
        }

        public RedisJournalStorageProvider Provider { get; }
        public IDatabase Database { get; }
        public IConnectionMultiplexer Connection { get; }
        public SiloLifecycleSubject Lifecycle { get; }
        public MetricCollector<long> Entries { get; }
        public MetricCollector<long> Pages { get; }
        public MetricCollector<long> Items { get; }

        public static async Task<Fixture> CreateAsync(bool customMapping = false)
        {
            var result = new Fixture(customMapping);
            await result.Lifecycle.OnStart(TestContext.Current.CancellationToken);
            return result;
        }

        private static async IAsyncEnumerable<RedisKey> Keys(
            IEnumerable<RedisKey> keys,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return key;
            }
        }

        public void Dispose()
        {
            Entries.Dispose();
            Pages.Dispose();
            Items.Dispose();
            _services.Dispose();
        }
    }
}
