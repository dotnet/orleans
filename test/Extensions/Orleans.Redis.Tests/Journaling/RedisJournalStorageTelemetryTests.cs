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
    public async Task Catalog_SeparatesOneLogicalEnumerationFromScanAndIdentityReads(bool customMapping)
    {
        using var fixture = await Fixture.CreateAsync(customMapping);
        var ids = new List<JournalId>();
        await foreach (var entry in fixture.Provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken))
        {
            ids.Add(entry.Id);
        }

        Assert.Equal(new[] { new JournalId("tenant/a"), new JournalId("tenant/b") }, ids);
        var operations = fixture.Operations.GetMeasurementSnapshot();
        Assert.Equal(2, operations.Count);
        Assert.True(operations[0].MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "redis"), new("operation", "initialize"), new("status", "ok") }));
        Assert.True(operations[1].MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "redis"), new("operation", "list"), new("status", "ok") }));
        Assert.Equal(2, fixture.Entries.GetMeasurementSnapshot().Sum(measurement => measurement.Value));
        var calls = fixture.ApiCalls.GetMeasurementSnapshot();
        Assert.Equal(customMapping ? 3 : 1, calls.Count);
        Assert.Single(calls, measurement => measurement.MatchesTags(
            new KeyValuePair<string, object?>[] { new("provider", "redis"), new("api", "scan_keys"), new("status", "ok") }));
        Assert.Equal(customMapping ? 2 : 0, calls.Count(measurement => Equals(measurement.Tags["api"], "hash_get")));
        Assert.Equal(2, Assert.Single(fixture.ApiItems.GetMeasurementSnapshot()).Value);
        Assert.All(calls, measurement => Assert.Equal(3, measurement.Tags.Count));
        Assert.All(fixture.ApiDuration.GetMeasurementSnapshot(), measurement => Assert.True(measurement.Value >= 0));
    }

    [Fact]
    public async Task Metadata_MissingJournalRecordsSuccessfulScriptAndLogicalNotFound()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(Task.FromResult(RedisResult.Create(new RedisValue[] { 0 })));

        Assert.Null(await fixture.Provider.CreateStorage(new("missing")).GetMetadataAsync(TestContext.Current.CancellationToken));
        var call = Assert.Single(fixture.ApiCalls.GetMeasurementSnapshot());
        Assert.True(call.MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "redis"), new("api", "script_evaluate"), new("status", "ok") }));
        var operation = fixture.Operations.GetMeasurementSnapshot()[1];
        Assert.True(operation.MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "redis"), new("operation", "get_metadata"), new("status", "not_found") }));
    }

    [Fact]
    public async Task Metadata_ScriptFailurePreservesExceptionAndRecordsBothLayers()
    {
        using var fixture = await Fixture.CreateAsync();
        var failure = new InvalidOperationException("private/key");
        fixture.Database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(Task.FromException<RedisResult>(failure));

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Provider.CreateStorage(new("private/key")).GetMetadataAsync(TestContext.Current.CancellationToken).AsTask()));
        Assert.True(Assert.Single(fixture.ApiCalls.GetMeasurementSnapshot()).MatchesTags(
            new KeyValuePair<string, object?>[] { new("provider", "redis"), new("api", "script_evaluate"), new("status", "error") }));
        var operation = fixture.Operations.GetMeasurementSnapshot()[1];
        Assert.Equal(3, operation.Tags.Count);
        Assert.True(operation.MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "redis"), new("operation", "get_metadata"), new("status", "error") }));
    }

    [Fact]
    public async Task Metadata_PreCanceledRequestRecordsNoSdkCall()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Provider.CreateStorage(new("canceled")).GetMetadataAsync(cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(fixture.ApiCalls.GetMeasurementSnapshot());
        Assert.True(fixture.Operations.GetMeasurementSnapshot()[1].MatchesTags(
            new KeyValuePair<string, object?>[] { new("provider", "redis"), new("operation", "get_metadata"), new("status", "canceled") }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Catalog_EarlyStopRecordsOneScanAndOneLogicalOutcome(bool cancel)
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using (var enumerator = fixture.Provider.ListAsync(cancellationToken: cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token))
        {
            Assert.True(await enumerator.MoveNextAsync());
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
            }
        }

        var call = Assert.Single(fixture.ApiCalls.GetMeasurementSnapshot());
        Assert.True(call.MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "redis"), new("api", "scan_keys"), new("status", "disposed") }));
        Assert.Equal(1, Assert.Single(fixture.ApiItems.GetMeasurementSnapshot()).Value);
        var operations = fixture.Operations.GetMeasurementSnapshot();
        Assert.Equal(2, operations.Count);
        Assert.True(operations[1].MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "redis"), new("operation", "list"), new("status", cancel ? "canceled" : "disposed") }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Close_RecordsSdkCallOnlyForOwnedConnection(bool shared)
    {
        using var fixture = await Fixture.CreateAsync(shared: shared);
        await fixture.Lifecycle.OnStop(TestContext.Current.CancellationToken);

        Assert.True(fixture.Operations.GetMeasurementSnapshot()[1].MatchesTags(
            new KeyValuePair<string, object?>[] { new("provider", "redis"), new("operation", "close"), new("status", "ok") }));
        if (shared)
        {
            Assert.Empty(fixture.ApiCalls.GetMeasurementSnapshot());
            await fixture.Connection.DidNotReceiveWithAnyArgs().CloseAsync();
        }
        else
        {
            Assert.True(Assert.Single(fixture.ApiCalls.GetMeasurementSnapshot()).MatchesTags(
                new KeyValuePair<string, object?>[] { new("provider", "redis"), new("api", "close"), new("status", "ok") }));
            await fixture.Connection.Received(1).CloseAsync();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services;

        private Fixture(bool customMapping, bool shared)
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            _services = services.BuildServiceProvider();
            var instruments = new OrleansInstruments(_services.GetRequiredService<IMeterFactory>());
            Operations = new(instruments.Meter, "orleans-journaling-provider-operations");
            Entries = new(instruments.Meter, "orleans-journaling-provider-catalog-entries");
            ApiCalls = new(instruments.Meter, "orleans-journaling-provider-api-calls");
            ApiItems = new(instruments.Meter, "orleans-journaling-provider-api-items");
            ApiDuration = new(instruments.Meter, "orleans-journaling-provider-api-call-duration");
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
                CreateMultiplexer = _ => Task.FromResult((Connection, shared))
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
                Database.HashGetAsync(key, RedisJournalStorage.JournalIdMetadataKey).Returns((RedisValue)value);
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
        public MetricCollector<long> Operations { get; }
        public MetricCollector<long> Entries { get; }
        public MetricCollector<long> ApiCalls { get; }
        public MetricCollector<long> ApiItems { get; }
        public MetricCollector<double> ApiDuration { get; }

        public static async Task<Fixture> CreateAsync(bool customMapping = false, bool shared = true)
        {
            var result = new Fixture(customMapping, shared);
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
            Operations.Dispose();
            Entries.Dispose();
            ApiCalls.Dispose();
            ApiItems.Dispose();
            ApiDuration.Dispose();
            _services.Dispose();
        }
    }
}
