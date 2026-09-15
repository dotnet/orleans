using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
public sealed class JournalStorageTelemetryTests
{
    [Fact]
    public void Instruments_RecordTypesUnitsValuesAndBoundedTags()
    {
        using var fixture = new Fixture();
        fixture.Telemetry.OnCatalogPage("s3", 3);
        fixture.Telemetry.OnCatalogItems("redis", 2);
        fixture.Telemetry.OnCatalogEntry("s3");
        fixture.Telemetry.OnRetry("s3", "metadata_conflict");

        Assert.Equal(new[]
        {
            "orleans-journaling-provider-catalog-entries",
            "orleans-journaling-provider-catalog-items",
            "orleans-journaling-provider-catalog-pages",
            "orleans-journaling-provider-retries",
        }, fixture.PublishedInstruments.Select(instrument => instrument.Name).Order());
        foreach (var instrument in new[] { fixture.CatalogPages.Instrument, fixture.CatalogItems.Instrument, fixture.CatalogEntries.Instrument, fixture.Retries.Instrument })
        {
            Assert.IsType<Counter<long>>(instrument);
            Assert.Equal("Microsoft.Orleans", instrument!.Meter.Name);
            Assert.Null(instrument.Unit);
        }

        var page = Assert.Single(fixture.CatalogPages.GetMeasurementSnapshot());
        Assert.Equal(1, page.Value);
        Assert.Single(page.Tags);
        Assert.True(page.MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "s3") }));
        var items = fixture.CatalogItems.GetMeasurementSnapshot();
        Assert.Equal(new long[] { 3, 2 }, items.Select(item => item.Value));
        Assert.All(items, item => Assert.Single(item.Tags));
        Assert.True(items[0].MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "s3") }));
        Assert.True(items[1].MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "redis") }));
        var entry = Assert.Single(fixture.CatalogEntries.GetMeasurementSnapshot());
        Assert.Equal(1, entry.Value);
        Assert.Single(entry.Tags);
        Assert.True(entry.MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "s3") }));
        var retry = Assert.Single(fixture.Retries.GetMeasurementSnapshot());
        Assert.Equal(1, retry.Value);
        Assert.Equal(2, retry.Tags.Count);
        Assert.True(retry.MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "s3"), new("reason", "metadata_conflict") }));
    }

    [Fact]
    public void CreateForDirectConstruction_ReturnsCachedRecorder()
    {
        Assert.Same(JournalStorageTelemetry.CreateForDirectConstruction(), JournalStorageTelemetry.CreateForDirectConstruction());
    }

    [Fact]
    public void CatalogPages_CountEmptyResponsesAndNativeItems()
    {
        using var fixture = new Fixture();
        fixture.Telemetry.OnCatalogPage("azure_blob", 0);
        fixture.Telemetry.OnCatalogPage("azure_blob", 2);
        Assert.Equal(new long[] { 1, 1 }, fixture.CatalogPages.GetMeasurementSnapshot().Select(item => item.Value));
        Assert.Equal(2, fixture.CatalogItems.GetMeasurementSnapshot().Sum(item => item.Value));
    }

    [Fact]
    public void CatalogItems_RecordConsumedItemsWithoutInventingPages()
    {
        using var fixture = new Fixture();
        fixture.Telemetry.OnCatalogItems("redis", 1);
        fixture.Telemetry.OnCatalogItems("redis", 1);
        Assert.Equal(new long[] { 1, 1 }, fixture.CatalogItems.GetMeasurementSnapshot().Select(item => item.Value));
        Assert.Empty(fixture.CatalogPages.GetMeasurementSnapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VolatileCatalog_CountsOnlyYieldedEntries(bool stopEarly)
    {
        using var fixture = new Fixture();
        var provider = new VolatileJournalStorageProvider(Options.Create(new JournaledStateManagerOptions()), fixture.Instruments);
        await provider.CreateStorage(new("a")).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await provider.CreateStorage(new("b")).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using (var enumerator = provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.Empty(fixture.CatalogEntries.GetMeasurementSnapshot());
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new JournalId("a"), enumerator.Current.Id);
            if (!stopEarly)
            {
                Assert.True(await enumerator.MoveNextAsync());
                Assert.Equal(new JournalId("b"), enumerator.Current.Id);
                Assert.False(await enumerator.MoveNextAsync());
            }
        }

        Assert.Equal(stopEarly ? 1 : 2, fixture.CatalogEntries.GetMeasurementSnapshot().Sum(measurement => measurement.Value));
        Assert.All(fixture.CatalogEntries.GetMeasurementSnapshot(), entry =>
        {
            Assert.Single(entry.Tags);
            Assert.True(entry.MatchesTags(new KeyValuePair<string, object?>[] { new("provider", "volatile") }));
        });
        Assert.Empty(fixture.CatalogPages.GetMeasurementSnapshot());
        Assert.Empty(fixture.CatalogItems.GetMeasurementSnapshot());
    }

    [Fact]
    public async Task VolatileCatalog_CancellationPreservesTokenAndYieldedCount()
    {
        using var fixture = new Fixture();
        var provider = new VolatileJournalStorageProvider(Options.Create(new JournaledStateManagerOptions()), fixture.Instruments);
        await provider.CreateStorage(new("a")).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await provider.CreateStorage(new("b")).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using (var enumerator = provider.ListAsync(cancellationToken: cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token))
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new JournalId("a"), enumerator.Current.Id);
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }

        Assert.Equal(1, Assert.Single(fixture.CatalogEntries.GetMeasurementSnapshot()).Value);
    }

    [Fact]
    public async Task VolatileCatalog_DisposedBeforeFirstAdvanceRecordsNoEntries()
    {
        using var fixture = new Fixture();
        var provider = new VolatileJournalStorageProvider(Options.Create(new JournaledStateManagerOptions()), fixture.Instruments);
        await provider.CreateStorage(new("unused")).CreateIfNotExistsAsync(cancellationToken: TestContext.Current.CancellationToken);
        await using (var enumerator = provider.ListAsync(cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.Empty(fixture.CatalogEntries.GetMeasurementSnapshot());
        }

        Assert.Empty(fixture.CatalogEntries.GetMeasurementSnapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VolatileCatalog_EmptyEnumerationRecordsNoEntries(bool emptyRange)
    {
        using var fixture = new Fixture();
        var provider = new VolatileJournalStorageProvider(Options.Create(new JournaledStateManagerOptions()), fixture.Instruments);
        if (emptyRange)
        {
            Assert.True(await provider.CreateStorage(new("a")).CreateIfNotExistsAsync(
                cancellationToken: TestContext.Current.CancellationToken));
        }

        var options = emptyRange ? new ListOptions { MinId = new("z"), MaxId = new("a") } : null;
        await using var enumerator = provider.ListAsync(options, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.False(await enumerator.MoveNextAsync());
        Assert.Empty(fixture.CatalogEntries.GetMeasurementSnapshot());
    }

    [Theory]
    [InlineData(JournalStorageTelemetry.S3, "s3")]
    [InlineData(JournalStorageTelemetry.AzureBlob, "azure_blob")]
    [InlineData(JournalStorageTelemetry.AzureTable, "azure_table")]
    [InlineData(JournalStorageTelemetry.Redis, "redis")]
    [InlineData(JournalStorageTelemetry.Volatile, "volatile")]
    public void ProviderConstants_UseBoundedNames(string provider, string expected)
    {
        Assert.Equal(expected, provider);
        using var fixture = new Fixture();
        fixture.Telemetry.OnCatalogEntry(provider);
        var entry = Assert.Single(fixture.CatalogEntries.GetMeasurementSnapshot());
        Assert.Equal(1, entry.Value);
        Assert.Equal(expected, Assert.Single(entry.Tags).Value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly MeterListener _listener;

        public Fixture()
        {
            var services = new ServiceCollection();
            services.AddMetrics();
            _services = services.BuildServiceProvider();
            Instruments = new OrleansInstruments(_services.GetRequiredService<IMeterFactory>());
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, _) =>
                {
                    if (ReferenceEquals(instrument.Meter, Instruments.Meter))
                    {
                        PublishedInstruments.Add(instrument);
                    }
                },
            };
            _listener.Start();
            Telemetry = new JournalStorageTelemetry(Instruments);
            CatalogEntries = new(Instruments.Meter, "orleans-journaling-provider-catalog-entries");
            CatalogPages = new(Instruments.Meter, "orleans-journaling-provider-catalog-pages");
            CatalogItems = new(Instruments.Meter, "orleans-journaling-provider-catalog-items");
            Retries = new(Instruments.Meter, "orleans-journaling-provider-retries");
        }

        public OrleansInstruments Instruments { get; }
        public List<Instrument> PublishedInstruments { get; } = [];
        public JournalStorageTelemetry Telemetry { get; }
        public MetricCollector<long> CatalogEntries { get; }
        public MetricCollector<long> CatalogPages { get; }
        public MetricCollector<long> CatalogItems { get; }
        public MetricCollector<long> Retries { get; }

        public void Dispose()
        {
            CatalogEntries.Dispose();
            CatalogPages.Dispose();
            CatalogItems.Dispose();
            Retries.Dispose();
            _listener.Dispose();
            _services.Dispose();
        }
    }
}
