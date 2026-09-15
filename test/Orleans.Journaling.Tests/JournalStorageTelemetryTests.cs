using System.Buffers;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
public sealed class JournalStorageTelemetryTests
{
    [Fact]
    public void Instruments_RecordTypesUnitsValuesAndBoundedTags()
    {
        using var fixture = new Fixture();
        fixture.Telemetry.OnOperationCompleted("s3", "append", TimeSpan.FromMilliseconds(7), "ok", 12);
        fixture.Telemetry.OnApiCallCompleted("s3", "put_object", TimeSpan.FromMilliseconds(5), "conflict", 3);
        fixture.Telemetry.OnRetry("s3", "metadata_conflict");

        Assert.IsType<Counter<long>>(fixture.Operations.Instrument);
        Assert.IsType<Histogram<double>>(fixture.OperationDuration.Instrument);
        Assert.Equal("ms", fixture.OperationDuration.Instrument!.Unit);
        Assert.Equal("bytes", fixture.OperationBytes.Instrument!.Unit);
        var operation = Assert.Single(fixture.Operations.GetMeasurementSnapshot());
        Assert.Equal(1, operation.Value);
        Assert.Equal(3, operation.Tags.Count);
        Assert.True(operation.MatchesTags(
            new KeyValuePair<string, object?>[] { new("provider", "s3"), new("operation", "append"), new("status", "ok") }));
        Assert.Equal(7, Assert.Single(fixture.OperationDuration.GetMeasurementSnapshot()).Value);
        Assert.Equal(12, Assert.Single(fixture.OperationBytes.GetMeasurementSnapshot()).Value);
        var api = Assert.Single(fixture.ApiCalls.GetMeasurementSnapshot());
        Assert.Equal(1, api.Value);
        Assert.Equal(3, api.Tags.Count);
        Assert.True(api.MatchesTags(
            new KeyValuePair<string, object?>[] { new("provider", "s3"), new("api", "put_object"), new("status", "conflict") }));
        Assert.Equal(5, Assert.Single(fixture.ApiDuration.GetMeasurementSnapshot()).Value);
        Assert.Equal(3, Assert.Single(fixture.ApiItems.GetMeasurementSnapshot()).Value);
        var retry = Assert.Single(fixture.Retries.GetMeasurementSnapshot());
        Assert.Equal(1, retry.Value);
        Assert.Equal(2, retry.Tags.Count);
        Assert.True(retry.MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "s3"), new("reason", "metadata_conflict") }));
    }

    [Fact]
    public async Task ApiCalls_RecordSuccessFailureCancellationAndExactLatency()
    {
        using var fixture = new Fixture();
        Assert.Equal(3, await fixture.Telemetry.TrackApiCallAsync(
            "redis", "hash_get",
            () =>
            {
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(7));
                return Task.FromResult(3);
            },
            countItems: static count => count));
        var failure = new InvalidOperationException("sensitive journal identity");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Telemetry.TrackApiCallAsync<int>("redis", "hash_get", () =>
            {
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(3));
                throw failure;
            })));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Telemetry.TrackApiCallAsync<int>("redis", "hash_get", () =>
            {
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(2));
                return Task.FromCanceled<int>(cancellation.Token);
            }));
        Assert.Equal(cancellation.Token, canceled.CancellationToken);

        var calls = fixture.ApiCalls.GetMeasurementSnapshot();
        Assert.Equal(3, calls.Count);
        foreach (var (call, status) in calls.Zip(new[] { "ok", "error", "canceled" }))
        {
            Assert.Equal(1, call.Value);
            Assert.Equal(3, call.Tags.Count);
            Assert.True(call.MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "redis"), new("api", "hash_get"), new("status", status) }));
        }

        Assert.Equal(new[] { 7d, 3d, 2d }, fixture.ApiDuration.GetMeasurementSnapshot().Select(measurement => measurement.Value));
        Assert.Equal(3, Assert.Single(fixture.ApiItems.GetMeasurementSnapshot()).Value);
    }

    [Fact]
    public async Task ApiPages_CountEmptyResponsesAndExcludeTerminalAdvanceAndConsumerDelay()
    {
        using var fixture = new Fixture();
        var source = fixture.Telemetry.TrackApiPages("azure_blob", "list_blobs", Pages(), static page => page.LongLength);
        await using var enumerator = source.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.Empty(fixture.ApiCalls.GetMeasurementSnapshot());
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Empty(enumerator.Current);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(new[] { 1, 2 }, enumerator.Current);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Equal(2, fixture.ApiCalls.GetMeasurementSnapshot().Count);
        Assert.Equal(new[] { 3d, 4d }, fixture.ApiDuration.GetMeasurementSnapshot().Select(measurement => measurement.Value));
        Assert.Equal(2, Assert.Single(fixture.ApiItems.GetMeasurementSnapshot()).Value);

        async IAsyncEnumerable<int[]> Pages()
        {
            await Task.CompletedTask;
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(3));
            yield return [];
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(4));
            yield return [1, 2];
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        }
    }

    [Fact]
    public async Task ApiPages_RecordFailureAfterSuccessfulPageWithoutRestart()
    {
        using var fixture = new Fixture();
        var failure = new InvalidOperationException("page failed");
        await using var enumerator = fixture.Telemetry.TrackApiPages(
            "azure_table", "query_entities", Pages(), static page => page.LongLength)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => enumerator.MoveNextAsync().AsTask()));
        var calls = fixture.ApiCalls.GetMeasurementSnapshot();
        Assert.Equal(2, calls.Count);
        Assert.True(calls[0].MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "azure_table"), new("api", "query_entities"), new("status", "ok") }));
        Assert.True(calls[1].MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "azure_table"), new("api", "query_entities"), new("status", "error") }));
        Assert.Equal(new[] { 2d, 5d }, fixture.ApiDuration.GetMeasurementSnapshot().Select(measurement => measurement.Value));

        async IAsyncEnumerable<int[]> Pages()
        {
            await Task.CompletedTask;
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(2));
            yield return [1];
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(5));
            throw failure;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Enumeration_RecordsOnceAndExcludesConsumerDelay(bool stopEarly)
    {
        using var fixture = new Fixture();
        var source = fixture.Telemetry.TrackCatalog("volatile", Entries());
        await using (var enumerator = source.GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.Empty(fixture.Operations.GetMeasurementSnapshot());
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new JournalId("a"), enumerator.Current.Id);
            fixture.Clock.Advance(TimeSpan.FromHours(1));
            if (!stopEarly)
            {
                Assert.True(await enumerator.MoveNextAsync());
                Assert.False(await enumerator.MoveNextAsync());
            }
        }

        var operation = Assert.Single(fixture.Operations.GetMeasurementSnapshot());
        Assert.True(operation.MatchesTags(
            new KeyValuePair<string, object?>[] { new("provider", "volatile"), new("operation", "list"), new("status", stopEarly ? "disposed" : "ok") }));
        Assert.Equal(stopEarly ? 3d : 5d, Assert.Single(fixture.OperationDuration.GetMeasurementSnapshot()).Value);
        Assert.Equal(stopEarly ? 1 : 2, fixture.CatalogEntries.GetMeasurementSnapshot().Sum(measurement => measurement.Value));

        async IAsyncEnumerable<JournalCatalogEntry> Entries()
        {
            try
            {
                await Task.CompletedTask;
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
                yield return new(new("a"));
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(2));
                yield return new(new("b"));
            }
            finally
            {
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(2));
            }
        }
    }

    [Fact]
    public async Task Enumeration_RecordsCancellationAndDisposalFailureExactlyOnce()
    {
        using var fixture = new Fixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using (var enumerator = fixture.Telemetry.TrackCatalog("volatile", Entries(cancellation.Token))
            .GetAsyncEnumerator(cancellation.Token))
        {
            Assert.True(await enumerator.MoveNextAsync());
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
        }

        Assert.True(Assert.Single(fixture.Operations.GetMeasurementSnapshot()).MatchesTags(
            new KeyValuePair<string, object?>[] { new("provider", "volatile"), new("operation", "list"), new("status", "canceled") }));

        var failure = new InvalidOperationException("dispose failed");
        var failing = fixture.Telemetry.TrackCatalog("volatile", FailingDisposal())
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await failing.MoveNextAsync());
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => failing.DisposeAsync().AsTask()));
        var records = fixture.Operations.GetMeasurementSnapshot();
        Assert.Equal(2, records.Count);
        Assert.True(records[1].MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "volatile"), new("operation", "list"), new("status", "error") }));

        async IAsyncEnumerable<JournalCatalogEntry> Entries([EnumeratorCancellation] CancellationToken token = default)
        {
            await Task.CompletedTask;
            yield return new(new("a"));
            token.ThrowIfCancellationRequested();
        }

        async IAsyncEnumerable<JournalCatalogEntry> FailingDisposal()
        {
            try
            {
                await Task.CompletedTask;
                yield return new(new("a"));
            }
            finally
            {
                throw failure;
            }
        }
    }

    [Fact]
    public async Task Enumeration_DisposedBeforeFirstAdvanceRecordsNoOperation()
    {
        using var fixture = new Fixture();
        var entered = 0;
        await using (var enumerator = fixture.Telemetry.TrackCatalog("volatile", Entries())
            .GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.Empty(fixture.Operations.GetMeasurementSnapshot());
        }

        Assert.Equal(0, entered);
        Assert.Empty(fixture.Operations.GetMeasurementSnapshot());
        Assert.Empty(fixture.CatalogEntries.GetMeasurementSnapshot());

        async IAsyncEnumerable<JournalCatalogEntry> Entries()
        {
            entered++;
            await Task.CompletedTask;
            yield return new(new("unused"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VolatileCatalog_EmptyEnumerationRecordsSuccessWithoutApiCalls(bool emptyRange)
    {
        using var fixture = new Fixture();
        var provider = new VolatileJournalStorageProvider(Options.Create(new JournaledStateManagerOptions()), fixture.Instruments);
        var options = emptyRange ? new ListOptions { MinId = new("z"), MaxId = new("a") } : null;
        await using var enumerator = provider.ListAsync(options, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.True(Assert.Single(fixture.Operations.GetMeasurementSnapshot()).MatchesTags(
            new KeyValuePair<string, object?>[] { new("provider", "volatile"), new("operation", "list"), new("status", "ok") }));
        Assert.Empty(fixture.CatalogEntries.GetMeasurementSnapshot());
        Assert.Empty(fixture.ApiCalls.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task InstrumentedRead_PreservesNullConsumerValidationAndRecordsError()
    {
        using var fixture = new Fixture();
        var provider = new VolatileJournalStorageProvider(Options.Create(new JournaledStateManagerOptions()), fixture.Instruments);
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            provider.CreateStorage(new("read")).ReadAsync(null!, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("consumer", exception.ParamName);
        Assert.True(Assert.Single(fixture.Operations.GetMeasurementSnapshot()).MatchesTags(
            new KeyValuePair<string, object?>[] { new("provider", "volatile"), new("operation", "read"), new("status", "error") }));
        Assert.Empty(fixture.OperationBytes.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task VolatileProvider_CoversAllStorageOperationsAndConditionalOutcomes()
    {
        using var fixture = new Fixture();
        var provider = new VolatileJournalStorageProvider(Options.Create(new JournaledStateManagerOptions()), fixture.Instruments);
        var storage = provider.CreateStorage(new("test/metrics"));
        var token = TestContext.Current.CancellationToken;
        Assert.Empty(fixture.Operations.GetMeasurementSnapshot());
        Assert.Null(await storage.GetMetadataAsync(token));
        Assert.True(await storage.CreateIfNotExistsAsync(cancellationToken: token));
        Assert.False(await storage.CreateIfNotExistsAsync(cancellationToken: token));
        var metadata = await storage.GetMetadataAsync(token);
        Assert.NotNull(metadata);
        Assert.Null(await storage.UpdateMetadataAsync(expectedETag: "stale", cancellationToken: token));
        Assert.NotNull(await storage.UpdateMetadataAsync(
            new Dictionary<string, string> { ["owner"] = "one" }, expectedETag: metadata.ETag, cancellationToken: token));
        await storage.AppendAsync(new ReadOnlySequence<byte>([1, 2, 3]), token);
        await storage.ReadAsync(new DiscardingConsumer(), token);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([4, 5]), token);
        await storage.DeleteAsync(token);

        string[] operations = ["get_metadata", "create", "create", "get_metadata", "update_metadata", "update_metadata", "append", "read", "replace", "delete"];
        string[] statuses = ["not_found", "ok", "already_exists", "ok", "not_applied", "ok", "ok", "ok", "ok", "ok"];
        var measurements = fixture.Operations.GetMeasurementSnapshot();
        Assert.Equal(operations.Length, measurements.Count);
        for (var index = 0; index < operations.Length; index++)
        {
            Assert.True(measurements[index].MatchesTags(
                new KeyValuePair<string, object?>[] { new("provider", "volatile"), new("operation", operations[index]), new("status", statuses[index]) }));
        }

        Assert.Equal(new long[] { 3, 3, 2 }, fixture.OperationBytes.GetMeasurementSnapshot().Select(measurement => measurement.Value));
        Assert.Empty(fixture.ApiCalls.GetMeasurementSnapshot());
    }

    [Theory]
    [InlineData(404, "not_found")]
    [InlineData(409, "conflict")]
    [InlineData(412, "conflict")]
    [InlineData(429, "throttled")]
    [InlineData(503, "unavailable")]
    [InlineData(504, "timeout")]
    [InlineData(200, "ok")]
    [InlineData(500, "error")]
    public void StatusClassifiers_UseBoundedOutcomes(int status, string expected)
    {
        Assert.Equal(expected, JournalStorageTelemetry.GetHttpStatus(status));
        Assert.Equal("conflict", JournalStorageTelemetry.GetExceptionStatus(new InconsistentStateException("stale")));
        Assert.Equal("timeout", JournalStorageTelemetry.GetExceptionStatus(new TimeoutException("slow")));
    }

    private sealed class DiscardingConsumer : IJournalStorageConsumer
    {
        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata) => buffer.Skip(buffer.Length);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services;

        public Fixture()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            _services = services.BuildServiceProvider();
            Instruments = new OrleansInstruments(_services.GetRequiredService<IMeterFactory>());
            Telemetry = new JournalStorageTelemetry(Instruments, Clock);
            Operations = new(Instruments.Meter, "orleans-journaling-provider-operations");
            OperationDuration = new(Instruments.Meter, "orleans-journaling-provider-operation-duration");
            OperationBytes = new(Instruments.Meter, "orleans-journaling-provider-operation-bytes");
            CatalogEntries = new(Instruments.Meter, "orleans-journaling-provider-catalog-entries");
            ApiCalls = new(Instruments.Meter, "orleans-journaling-provider-api-calls");
            ApiDuration = new(Instruments.Meter, "orleans-journaling-provider-api-call-duration");
            ApiItems = new(Instruments.Meter, "orleans-journaling-provider-api-items");
            Retries = new(Instruments.Meter, "orleans-journaling-provider-retries");
        }

        public OrleansInstruments Instruments { get; }
        public FakeTimeProvider Clock { get; } = new();
        public JournalStorageTelemetry Telemetry { get; }
        public MetricCollector<long> Operations { get; }
        public MetricCollector<double> OperationDuration { get; }
        public MetricCollector<long> OperationBytes { get; }
        public MetricCollector<long> CatalogEntries { get; }
        public MetricCollector<long> ApiCalls { get; }
        public MetricCollector<double> ApiDuration { get; }
        public MetricCollector<long> ApiItems { get; }
        public MetricCollector<long> Retries { get; }

        public void Dispose()
        {
            Operations.Dispose();
            OperationDuration.Dispose();
            OperationBytes.Dispose();
            CatalogEntries.Dispose();
            ApiCalls.Dispose();
            ApiDuration.Dispose();
            ApiItems.Dispose();
            Retries.Dispose();
            _services.Dispose();
        }
    }
}
