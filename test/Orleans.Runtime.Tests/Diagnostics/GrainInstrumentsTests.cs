using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class GrainInstrumentsTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void GrainInstruments_RecordsMetricsUsingMeterFactory()
    {
        using var fixture = new GrainMetricFixture();
        var instruments = fixture.Instruments;
        var metrics = fixture.Register("grain", "Example.Grain");
        using var grainCountsCollector = new MetricCollector<int>(fixture.MeterFactory, "Microsoft.Orleans", InstrumentNames.GRAIN_COUNTS);
        using var systemTargetCountsCollector = new MetricCollector<int>(fixture.MeterFactory, "Microsoft.Orleans", InstrumentNames.SYSTEM_TARGET_COUNTS);

        instruments.IncrementGrainCounts(metrics);
        instruments.DecrementGrainCounts(metrics);
        instruments.IncrementSystemTargetCounts("system-target");
        instruments.DecrementSystemTargetCounts("system-target");

        Assert.Collection(
            grainCountsCollector.GetMeasurementSnapshot(),
            measurement => Assert.Equal(1, measurement.Value),
            measurement => Assert.Equal(-1, measurement.Value));
        Assert.Collection(
            systemTargetCountsCollector.GetMeasurementSnapshot(),
            measurement => Assert.Equal(1, measurement.Value),
            measurement => Assert.Equal(-1, measurement.Value));
        Assert.Equal(0, metrics.GrainCount);
        Assert.Empty(new GrainCountStatistics(fixture.Catalog).GetSimpleGrainStatistics());
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void GrainCounts_SeparateCanonicalTagsFromClrKeys()
    {
        using var fixture = new GrainMetricFixture();
        var first = fixture.Register("named-orders", "Example.OrderGrain");
        var second = fixture.Register("named-invoices", "Example.InvoiceGrain");
        var unrelated = fixture.Register("untouched", "Example.UnrelatedGrain");
        fixture.Instruments.IncrementGrainCounts(unrelated);
        fixture.Instruments.IncrementGrainCounts(unrelated);
        var observations = new List<GrainMeasurement>();
        using var listener = fixture.Listen((instrument, value, tags, _) => observations.Add(new(instrument, value, tags.ToArray())));

        fixture.Instruments.IncrementGrainCounts(first);
        fixture.Instruments.IncrementGrainCounts(first);
        fixture.Instruments.IncrementGrainCounts(second);
        fixture.Instruments.DecrementGrainCounts(first);
        fixture.Instruments.DecrementGrainCounts(second);

        Assert.Collection(observations,
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", first.GrainTypeTagValue),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", first.GrainTypeTagValue),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", second.GrainTypeTagValue),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, -1, "grain_type", first.GrainTypeTagValue),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, -1, "grain_type", second.GrainTypeTagValue));
        Assert.Equal(
            new[] { new KeyValuePair<string, int>("named-invoices", 0), new("named-orders", 1), new("untouched", 2) },
            fixture.Catalog.GrainTypes
                .Select(metrics => new KeyValuePair<string, int>(metrics.GrainTypeTagValue, metrics.GrainCount))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(
            new[] { new KeyValuePair<string, long>("Example.OrderGrain", 1), new("Example.UnrelatedGrain", 2) },
            new GrainCountStatistics(fixture.Catalog).GetSimpleGrainStatistics().OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void SystemTargetCounts_RetainTypeTagsWithoutAffectingGrainStatistics()
    {
        using var fixture = new GrainMetricFixture();
        var grain = fixture.Register("interleaved-orders", "Example.OrderGrain");
        var target = new string("Example.CatalogSystemTarget".ToCharArray());
        var observations = new List<GrainMeasurement>();
        using var listener = fixture.Listen((instrument, value, tags, _) => observations.Add(new(instrument, value, tags.ToArray())));

        fixture.Instruments.IncrementGrainCounts(grain);
        fixture.Instruments.IncrementSystemTargetCounts(target);
        fixture.Instruments.DecrementGrainCounts(grain);
        fixture.Instruments.DecrementSystemTargetCounts(target);

        Assert.Collection(observations,
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", grain.GrainTypeTagValue),
            item => AssertGrainMeasurement(item, InstrumentNames.SYSTEM_TARGET_COUNTS, 1, "type", target),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, -1, "grain_type", grain.GrainTypeTagValue),
            item => AssertGrainMeasurement(item, InstrumentNames.SYSTEM_TARGET_COUNTS, -1, "type", target));
        Assert.Same(grain, Assert.Single(fixture.Catalog.GrainTypes));
        Assert.Equal("Example.OrderGrain", grain.GrainClassName);
        Assert.Equal(0, grain.GrainCount);
        Assert.Empty(new GrainCountStatistics(fixture.Catalog).GetSimpleGrainStatistics());
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void SimpleGrainStatistics_FiltersNonpositiveClrCounts()
    {
        using var fixture = new GrainMetricFixture();
        var instruments = fixture.Instruments;
        var statistics = new GrainCountStatistics(fixture.Catalog);
        Assert.Empty(statistics.GetSimpleGrainStatistics());
        var orders = fixture.Register("orders", "Example.OrderGrain");
        var invoices = fixture.Register("invoices", "Example.InvoiceGrain");
        var zero = fixture.Register("zero", "Example.ZeroGrain");
        var negative = fixture.Register("negative", "Example.NegativeGrain");
        instruments.IncrementGrainCounts(orders);
        instruments.IncrementGrainCounts(orders);
        instruments.IncrementGrainCounts(invoices);
        instruments.IncrementGrainCounts(zero);
        instruments.DecrementGrainCounts(zero);
        instruments.DecrementGrainCounts(negative);
        instruments.IncrementSystemTargetCounts("Example.CatalogSystemTarget");

        var snapshot = statistics.GetSimpleGrainStatistics().OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { new KeyValuePair<string, long>("Example.InvoiceGrain", 1L), new("Example.OrderGrain", 2L) },
            snapshot);
        Assert.Equal(4, fixture.Catalog.GrainTypes.Count());
        Assert.Equal(0, zero.GrainCount);
        Assert.Equal(-1, negative.GrainCount);
        Assert.False(fixture.Catalog.TryGetGrainTypeMetrics(GrainType.Create("missing"), out _));

        instruments.DecrementGrainCounts(orders);
        instruments.DecrementGrainCounts(orders);
        Assert.Equal(new KeyValuePair<string, long>("Example.InvoiceGrain", 1L), Assert.Single(statistics.GetSimpleGrainStatistics()));
        Assert.Equal(0, orders.GrainCount);
        Assert.Equal(-1, negative.GrainCount);
        Assert.Equal(new KeyValuePair<string, long>("Example.OrderGrain", 2L), snapshot[1]);
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void SimpleGrainStatistics_SeparatesMetadataRegistrationFromInstances()
    {
        using var fixture = new GrainMetricFixture();
        var statistics = new GrainCountStatistics(fixture.Catalog);
        var registry = fixture.Catalog.GrainTypes;
        Assert.Empty(registry);
        var metadataOnly = fixture.Catalog.GetGrainTypeMetrics(GrainType.Create("metadata-only"));
        var registered = fixture.Register("registered", "Example.Grain");
        Assert.Null(metadataOnly.GrainClassName);
        Assert.Equal("Example.Grain", registered.GrainClassName);
        Assert.Equal(2, registry.Count());
        foreach (var metrics in registry)
        {
            Assert.Equal(0, metrics.GrainCount);
            Assert.Equal(0, metrics.ActivationCount.Value);
            Assert.Equal(0, metrics.WorkingSetCount.Value);
        }
        var emptySnapshot = statistics.GetSimpleGrainStatistics();
        Assert.Empty(emptySnapshot);

        metadataOnly.OnActivationAdded();
        metadataOnly.OnWorkingSetAdded();
        fixture.Instruments.IncrementGrainCounts(metadataOnly);
        Assert.Equal(1, metadataOnly.GrainCount);
        Assert.Empty(statistics.GetSimpleGrainStatistics());
        fixture.Instruments.IncrementGrainCounts(registered);
        Assert.Equal(1, registered.GrainCount);
        Assert.Equal(0, registered.ActivationCount.Value);
        Assert.Equal(0, registered.WorkingSetCount.Value);
        Assert.Equal(new KeyValuePair<string, long>("Example.Grain", 1), Assert.Single(statistics.GetSimpleGrainStatistics()));
        Assert.Empty(emptySnapshot);
        Assert.Equal(1, metadataOnly.ActivationCount.Value);
        Assert.Equal(1, metadataOnly.WorkingSetCount.Value);

        fixture.Instruments.DecrementGrainCounts(metadataOnly);
        fixture.Instruments.DecrementGrainCounts(registered);
        Assert.Empty(statistics.GetSimpleGrainStatistics());
        Assert.All(registry, metrics => Assert.Equal(0, metrics.GrainCount));
        Assert.Equal(2, registry.Count());
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void SimpleGrainStatistics_IsInstanceLocalWithoutListener()
    {
        using var first = new GrainMetricFixture();
        using var second = new GrainMetricFixture();
        var firstStatistics = new GrainCountStatistics(first.Catalog);
        var secondStatistics = new GrainCountStatistics(second.Catalog);
        const string clrName = "Example.SharedGrain";
        Assert.Empty(firstStatistics.GetSimpleGrainStatistics());
        Assert.Empty(secondStatistics.GetSimpleGrainStatistics());
        var firstCount = first.Register("orders", clrName);
        var secondCount = second.Register("orders", clrName);
        Assert.NotSame(firstCount, secondCount);

        first.Instruments.IncrementGrainCounts(firstCount);
        second.Instruments.IncrementGrainCounts(secondCount);
        second.Instruments.IncrementGrainCounts(secondCount);
        Assert.Equal(new KeyValuePair<string, long>(clrName, 1L), Assert.Single(firstStatistics.GetSimpleGrainStatistics()));
        Assert.Equal(new KeyValuePair<string, long>(clrName, 2L), Assert.Single(secondStatistics.GetSimpleGrainStatistics()));
        Assert.Same(firstCount, Assert.Single(first.Catalog.GrainTypes));
        Assert.Same(secondCount, Assert.Single(second.Catalog.GrainTypes));

        first.Instruments.DecrementGrainCounts(firstCount);
        Assert.Empty(firstStatistics.GetSimpleGrainStatistics());
        Assert.Equal(0, firstCount.GrainCount);
        Assert.Equal(new KeyValuePair<string, long>(clrName, 2L), Assert.Single(secondStatistics.GetSimpleGrainStatistics()));
        second.Instruments.DecrementGrainCounts(secondCount);
        Assert.Equal(new KeyValuePair<string, long>(clrName, 1L), Assert.Single(secondStatistics.GetSimpleGrainStatistics()));
        Assert.Empty(firstStatistics.GetSimpleGrainStatistics());
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void GrainCounts_CanonicalAliasesHaveDistinctCountersAndCombinedClrStatistics()
    {
        using var fixture = new GrainMetricFixture();
        var instruments = fixture.Instruments;
        const string clrName = "Example.SharedGrain";
        var first = fixture.Register("orders", clrName);
        var alias = fixture.Register("orders-alias", new string(clrName.ToCharArray()));
        var unrelated = fixture.Register("unrelated", "Example.UnrelatedGrain");
        Assert.NotSame(first, alias);
        Assert.NotSame(first, unrelated);
        Assert.Equal(0, first.GrainCount);
        Assert.Equal(0, alias.GrainCount);
        var statistics = new GrainCountStatistics(fixture.Catalog);
        Assert.Empty(statistics.GetSimpleGrainStatistics());
        var observations = new List<GrainMeasurement>();
        using var listener = fixture.Listen((instrument, value, tags, _) => observations.Add(new(instrument, value, tags.ToArray())));

        instruments.IncrementGrainCounts(first);
        instruments.IncrementGrainCounts(alias);
        instruments.IncrementGrainCounts(alias);
        instruments.DecrementGrainCounts(first);

        Assert.Collection(observations,
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", first.GrainTypeTagValue),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", alias.GrainTypeTagValue),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", alias.GrainTypeTagValue),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, -1, "grain_type", first.GrainTypeTagValue));
        Assert.Equal(0, first.GrainCount);
        Assert.Equal(2, alias.GrainCount);
        Assert.Equal(0, unrelated.GrainCount);
        Assert.Equal(new KeyValuePair<string, long>(clrName, 2), Assert.Single(statistics.GetSimpleGrainStatistics()));

        instruments.DecrementGrainCounts(first);
        Assert.Equal(-1, first.GrainCount);
        Assert.Equal(2, alias.GrainCount);
        Assert.Equal(new KeyValuePair<string, long>(clrName, 1), Assert.Single(statistics.GetSimpleGrainStatistics()));
        instruments.DecrementGrainCounts(alias);
        Assert.Equal(1, alias.GrainCount);
        Assert.Empty(statistics.GetSimpleGrainStatistics());
        instruments.DecrementGrainCounts(alias);
        Assert.Equal(0, alias.GrainCount);
        Assert.Empty(statistics.GetSimpleGrainStatistics());
        instruments.IncrementGrainCounts(first);
        Assert.Equal(0, first.GrainCount);
        Assert.Empty(statistics.GetSimpleGrainStatistics());
        Assert.Collection(observations.Skip(4),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, -1, "grain_type", first.GrainTypeTagValue),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, -1, "grain_type", alias.GrainTypeTagValue),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, -1, "grain_type", alias.GrainTypeTagValue),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", first.GrainTypeTagValue));
        Assert.Same(first, fixture.Catalog.GetGrainTypeMetrics(GrainType.Create("orders")));
        Assert.Same(alias, fixture.Catalog.GetGrainTypeMetrics(GrainType.Create("orders-alias")));
        Assert.Equal(3, fixture.Catalog.GrainTypes.Count());
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void GrainCounts_ConcurrentRegistrationAndUpdatesShareMetadata()
    {
        using var fixture = new GrainMetricFixture();
        var instruments = fixture.Instruments;
        const string clrName = "Example.SharedGrain";
        const int instancesPerWorker = 256;
        var counters = new GrainTypeMetrics[32];

        Parallel.For(0, counters.Length, worker =>
        {
            var counter = fixture.Register(new string("orders".ToCharArray()), new string(clrName.ToCharArray()));
            counters[worker] = counter;
            for (var i = 0; i < instancesPerWorker; i++)
            {
                instruments.IncrementGrainCounts(counter);
            }
        });

        var entry = Assert.Single(fixture.Catalog.GrainTypes);
        Assert.Equal(clrName, entry.GrainClassName);
        Assert.Equal("orders", entry.GrainTypeTagValue);
        Assert.All(counters, counter => Assert.Same(entry, counter));
        Assert.Equal(counters.Length * instancesPerWorker, entry.GrainCount);
        var statistics = new GrainCountStatistics(fixture.Catalog);
        Assert.Equal(new KeyValuePair<string, long>(clrName, counters.Length * instancesPerWorker), Assert.Single(statistics.GetSimpleGrainStatistics()));

        Parallel.For(0, counters.Length, worker =>
        {
            for (var i = 0; i < instancesPerWorker; i++)
            {
                instruments.DecrementGrainCounts(counters[worker]);
            }
        });

        Assert.Equal(0, entry.GrainCount);
        Assert.Empty(statistics.GetSimpleGrainStatistics());
        Assert.Same(entry, fixture.Catalog.GetGrainTypeMetrics(GrainType.Create("orders")));
        Assert.Same(entry, Assert.Single(fixture.Catalog.GrainTypes));
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(true)]
    [InlineData(false)]
    public void WarmGrainRecording_AllocatesZeroWithAndWithoutLiveCallbacks(bool enabled)
    {
        using var fixture = new GrainMetricFixture();
        var first = fixture.Register("warm-orders", "Example.OrderGrain");
        var second = fixture.Register("warm-invoices", "Example.InvoiceGrain");
        var target = new string("Example.WarmSystemTarget".ToCharArray());
        fixture.Instruments.IncrementGrainCounts(first);
        fixture.Instruments.IncrementGrainCounts(first);
        fixture.Instruments.IncrementGrainCounts(second);
        var positives = new int[3];
        var negatives = new int[3];
        var mismatches = 0;
        using var listener = fixture.Listen((instrument, value, tags, _) =>
        {
            var isSystemTarget = instrument.Name == InstrumentNames.SYSTEM_TARGET_COUNTS;
            var index = isSystemTarget ? 2 : tags.Length == 1 && ReferenceEquals(tags[0].Value, first.GrainTypeTagValue) ? 0 : 1;
            if (instrument is not UpDownCounter<int> || tags.Length != 1
                || tags[0].Key != (isSystemTarget ? "type" : "grain_type")
                || !ReferenceEquals(tags[0].Value, index == 0 ? first.GrainTypeTagValue : index == 1 ? second.GrainTypeTagValue : target))
            {
                mismatches++;
            }
            if (value == 1) positives[index]++;
            else if (value == -1) negatives[index]++;
            else mismatches++;
        }, enabled);
        Assert.Equal(2, fixture.PublishedInstruments.Count);
        Assert.All(fixture.PublishedInstruments, instrument => Assert.Equal(enabled, instrument.Enabled));
        void RecordPair()
        {
            fixture.Instruments.IncrementGrainCounts(first);
            fixture.Instruments.DecrementGrainCounts(first);
            fixture.Instruments.IncrementGrainCounts(second);
            fixture.Instruments.DecrementGrainCounts(second);
            fixture.Instruments.IncrementSystemTargetCounts(target);
            fixture.Instruments.DecrementSystemTargetCounts(target);
        }
        for (var i = 0; i < 1024; i++) RecordPair();
        Array.Clear(positives);
        Array.Clear(negatives);
        mismatches = 0;
        const int iterations = 4096;

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++) RecordPair();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.Equal(0, mismatches);
        Assert.Equal(enabled ? new[] { iterations, iterations, iterations } : new int[3], positives);
        Assert.Equal(enabled ? new[] { iterations, iterations, iterations } : new int[3], negatives);
        Assert.Equal(2, fixture.Catalog.GrainTypes.Count());
        Assert.Equal(2, first.GrainCount);
        Assert.Equal(1, second.GrainCount);
        if (!enabled)
        {
            // Late enabling has no replay; fresh operations both emit and still update CLR bookkeeping.
            foreach (var instrument in fixture.PublishedInstruments) listener.EnableMeasurementEvents(instrument);
            Assert.Equal(new int[3], positives);
            Assert.Equal(new int[3], negatives);
            RecordPair();
            Assert.Equal(new[] { 1, 1, 1 }, positives);
            Assert.Equal(new[] { 1, 1, 1 }, negatives);
        }
        fixture.Instruments.IncrementGrainCounts(first);
        Assert.Equal(3, first.GrainCount);
        Assert.Equal(1, second.GrainCount);
        Assert.Equal(enabled ? iterations + 1 : 2, positives[0]);
        Assert.Equal(0, mismatches);
    }

    private static void AssertGrainMeasurement(GrainMeasurement item, string instrumentName, int value, string tagName, string type)
    {
        Assert.IsType<UpDownCounter<int>>(item.Instrument);
        Assert.Equal(instrumentName, item.Instrument.Name);
        Assert.Equal(value, item.Value);
        var tag = Assert.Single(item.Tags);
        Assert.Equal(tagName, tag.Key);
        Assert.Same(type, Assert.IsType<string>(tag.Value));
    }

    private sealed record GrainMeasurement(Instrument Instrument, int Value, KeyValuePair<string, object?>[] Tags);

    private sealed class GrainMetricFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly Meter _meter;
        public GrainInstruments Instruments { get; }
        public CatalogInstruments Catalog { get; }
        public IMeterFactory MeterFactory => _provider.GetRequiredService<IMeterFactory>();
        public List<Instrument> PublishedInstruments { get; } = new();

        public GrainMetricFixture()
        {
            _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var orleans = new OrleansInstruments(_provider.GetRequiredService<IMeterFactory>());
            _meter = orleans.Meter;
            Instruments = new GrainInstruments(orleans);
            Catalog = new CatalogInstruments(orleans);
        }

        public GrainTypeMetrics Register(string canonicalName, string clrName)
        {
            var metrics = Catalog.GetGrainTypeMetrics(GrainType.Create(canonicalName));
            Interlocked.CompareExchange(ref metrics.GrainClassName, clrName, null);
            return metrics;
        }

        public MeterListener Listen(MeasurementCallback<int> callback, bool enable = true)
        {
            var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (ReferenceEquals(instrument.Meter, _meter)
                    && instrument.Name is InstrumentNames.GRAIN_COUNTS or InstrumentNames.SYSTEM_TARGET_COUNTS)
                {
                    PublishedInstruments.Add(instrument);
                    if (enable) owner.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback(callback);
            listener.Start();
            return listener;
        }

        public void Dispose() => _provider.Dispose();
    }
}
