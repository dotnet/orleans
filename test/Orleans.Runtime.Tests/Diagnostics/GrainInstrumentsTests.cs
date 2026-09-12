using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Tester.Diagnostics;

public class GrainInstrumentsTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void GrainInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new GrainInstruments(new OrleansInstruments(meterFactory));
        var grainCount = instruments.GetGrainCount("Example.Grain");
        using var grainCountsCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.GRAIN_COUNTS);
        using var systemTargetCountsCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.SYSTEM_TARGET_COUNTS);

        instruments.IncrementGrainCounts("grain", grainCount);
        instruments.DecrementGrainCounts("grain", grainCount);
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
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void GrainCounts_SeparateCanonicalTagsFromClrKeys()
    {
        using var fixture = new GrainMetricFixture();
        var first = new GrainTypeMetrics(GrainType.Create("named-orders")).GrainTypeTagValue;
        var second = new GrainTypeMetrics(GrainType.Create("named-invoices")).GrainTypeTagValue;
        var firstCount = fixture.Instruments.GetGrainCount("Example.OrderGrain");
        var secondCount = fixture.Instruments.GetGrainCount("Example.InvoiceGrain");
        var unrelatedCount = fixture.Instruments.GetGrainCount("Example.UnrelatedGrain");
        fixture.Instruments.IncrementGrainCounts("untouched", unrelatedCount);
        fixture.Instruments.IncrementGrainCounts("untouched", unrelatedCount);
        var observations = new List<GrainMeasurement>();
        using var listener = fixture.Listen((instrument, value, tags, _) => observations.Add(new(instrument, value, tags.ToArray())));

        fixture.Instruments.IncrementGrainCounts(first, firstCount);
        fixture.Instruments.IncrementGrainCounts(first, firstCount);
        fixture.Instruments.IncrementGrainCounts(second, secondCount);
        fixture.Instruments.DecrementGrainCounts(first, firstCount);
        fixture.Instruments.DecrementGrainCounts(second, secondCount);

        Assert.Collection(observations,
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", first),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", first),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", second),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, -1, "grain_type", first),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, -1, "grain_type", second));
        Assert.Equal(
            new[] { new KeyValuePair<string, int>("Example.InvoiceGrain", 0), new("Example.OrderGrain", 1), new("Example.UnrelatedGrain", 2) },
            fixture.Instruments.GrainCounts
                .Select(pair => new KeyValuePair<string, int>(pair.Key, pair.Value.Value))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void SystemTargetCounts_RetainTypeTagsWithoutAffectingGrainStatistics()
    {
        using var fixture = new GrainMetricFixture();
        var grain = new GrainTypeMetrics(GrainType.Create("interleaved-orders")).GrainTypeTagValue;
        var grainCount = fixture.Instruments.GetGrainCount("Example.OrderGrain");
        var target = new string("Example.CatalogSystemTarget".ToCharArray());
        var observations = new List<GrainMeasurement>();
        using var listener = fixture.Listen((instrument, value, tags, _) => observations.Add(new(instrument, value, tags.ToArray())));

        fixture.Instruments.IncrementGrainCounts(grain, grainCount);
        fixture.Instruments.IncrementSystemTargetCounts(target);
        fixture.Instruments.DecrementGrainCounts(grain, grainCount);
        fixture.Instruments.DecrementSystemTargetCounts(target);

        Assert.Collection(observations,
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", grain),
            item => AssertGrainMeasurement(item, InstrumentNames.SYSTEM_TARGET_COUNTS, 1, "type", target),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, -1, "grain_type", grain),
            item => AssertGrainMeasurement(item, InstrumentNames.SYSTEM_TARGET_COUNTS, -1, "type", target));
        var count = Assert.Single(fixture.Instruments.GrainCounts);
        Assert.Equal("Example.OrderGrain", count.Key);
        Assert.Equal(0, count.Value.Value);
        Assert.Empty(new GrainCountStatistics(fixture.Instruments).GetSimpleGrainStatistics());
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void SimpleGrainStatistics_FiltersNonpositiveClrCounts()
    {
        using var fixture = new GrainMetricFixture();
        var instruments = fixture.Instruments;
        var statistics = new GrainCountStatistics(instruments);
        Assert.Empty(statistics.GetSimpleGrainStatistics());
        var orders = instruments.GetGrainCount("Example.OrderGrain");
        var invoices = instruments.GetGrainCount("Example.InvoiceGrain");
        var zero = instruments.GetGrainCount("Example.ZeroGrain");
        var negative = instruments.GetGrainCount("Example.NegativeGrain");
        instruments.IncrementGrainCounts("orders", orders);
        instruments.IncrementGrainCounts("orders", orders);
        instruments.IncrementGrainCounts("invoices", invoices);
        instruments.IncrementGrainCounts("zero", zero);
        instruments.DecrementGrainCounts("zero", zero);
        instruments.DecrementGrainCounts("negative", negative);
        instruments.IncrementSystemTargetCounts("Example.CatalogSystemTarget");

        var snapshot = statistics.GetSimpleGrainStatistics().OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { new KeyValuePair<string, long>("Example.InvoiceGrain", 1L), new("Example.OrderGrain", 2L) },
            snapshot);
        Assert.Equal(4, instruments.GrainCounts.Count);
        Assert.Equal(0, instruments.GrainCounts["Example.ZeroGrain"].Value);
        Assert.Equal(-1, instruments.GrainCounts["Example.NegativeGrain"].Value);
        Assert.False(instruments.GrainCounts.ContainsKey("Example.MissingGrain"));

        instruments.DecrementGrainCounts("orders", orders);
        instruments.DecrementGrainCounts("orders", orders);
        Assert.Equal(new KeyValuePair<string, long>("Example.InvoiceGrain", 1L), Assert.Single(statistics.GetSimpleGrainStatistics()));
        Assert.Equal(0, instruments.GrainCounts["Example.OrderGrain"].Value);
        Assert.Equal(-1, instruments.GrainCounts["Example.NegativeGrain"].Value);
        Assert.Equal(new KeyValuePair<string, long>("Example.OrderGrain", 2L), snapshot[1]);
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void SimpleGrainStatistics_IsInstanceLocalWithoutListener()
    {
        using var first = new GrainMetricFixture();
        using var second = new GrainMetricFixture();
        var firstStatistics = new GrainCountStatistics(first.Instruments);
        var secondStatistics = new GrainCountStatistics(second.Instruments);
        const string clrName = "Example.SharedGrain";
        Assert.Empty(firstStatistics.GetSimpleGrainStatistics());
        Assert.Empty(secondStatistics.GetSimpleGrainStatistics());
        var firstCount = first.Instruments.GetGrainCount(clrName);
        var secondCount = second.Instruments.GetGrainCount(clrName);
        Assert.NotSame(firstCount, secondCount);

        first.Instruments.IncrementGrainCounts("first-silo-type", firstCount);
        second.Instruments.IncrementGrainCounts("second-silo-type", secondCount);
        second.Instruments.IncrementGrainCounts("second-silo-type", secondCount);
        Assert.Equal(new KeyValuePair<string, long>(clrName, 1L), Assert.Single(firstStatistics.GetSimpleGrainStatistics()));
        Assert.Equal(new KeyValuePair<string, long>(clrName, 2L), Assert.Single(secondStatistics.GetSimpleGrainStatistics()));
        Assert.NotSame(first.Instruments.GrainCounts, second.Instruments.GrainCounts);

        first.Instruments.DecrementGrainCounts("first-silo-type", firstCount);
        Assert.Empty(firstStatistics.GetSimpleGrainStatistics());
        Assert.Equal(0, first.Instruments.GrainCounts[clrName].Value);
        Assert.Equal(new KeyValuePair<string, long>(clrName, 2L), Assert.Single(secondStatistics.GetSimpleGrainStatistics()));
        second.Instruments.DecrementGrainCounts("second-silo-type", secondCount);
        Assert.Equal(new KeyValuePair<string, long>(clrName, 1L), Assert.Single(secondStatistics.GetSimpleGrainStatistics()));
        Assert.Empty(firstStatistics.GetSimpleGrainStatistics());
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void GrainCounts_CanonicalAliasesShareClrCounter()
    {
        using var fixture = new GrainMetricFixture();
        var instruments = fixture.Instruments;
        const string clrName = "Example.SharedGrain";
        var first = instruments.GetGrainCount(clrName);
        var alias = instruments.GetGrainCount(new string(clrName.ToCharArray()));
        var unrelated = instruments.GetGrainCount("Example.UnrelatedGrain");
        Assert.Same(first, alias);
        Assert.NotSame(first, unrelated);
        Assert.Equal(0, first.Value);
        var statistics = new GrainCountStatistics(instruments);
        Assert.Empty(statistics.GetSimpleGrainStatistics());
        var observations = new List<GrainMeasurement>();
        using var listener = fixture.Listen((instrument, value, tags, _) => observations.Add(new(instrument, value, tags.ToArray())));

        instruments.IncrementGrainCounts("orders", first);
        instruments.IncrementGrainCounts("orders-alias", alias);
        instruments.IncrementGrainCounts("orders-alias", alias);
        instruments.DecrementGrainCounts("orders", first);

        Assert.Collection(observations,
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", "orders"),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", "orders-alias"),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", "orders-alias"),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, -1, "grain_type", "orders"));
        Assert.Equal(2, first.Value);
        Assert.Equal(0, unrelated.Value);
        Assert.Equal(new KeyValuePair<string, long>(clrName, 2), Assert.Single(statistics.GetSimpleGrainStatistics()));

        instruments.DecrementGrainCounts("orders-alias", alias);
        instruments.DecrementGrainCounts("orders-alias", alias);
        Assert.Equal(0, first.Value);
        Assert.Empty(statistics.GetSimpleGrainStatistics());
        Assert.Same(first, instruments.GetGrainCount(clrName));
        Assert.Equal(2, instruments.GrainCounts.Count);
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void GrainCounts_ConcurrentRegistrationAndUpdatesShareCounter()
    {
        using var fixture = new GrainMetricFixture();
        var instruments = fixture.Instruments;
        const string clrName = "Example.SharedGrain";
        const int instancesPerWorker = 256;
        var counters = new StrongBox<int>[32];

        Parallel.For(0, counters.Length, worker =>
        {
            var counter = instruments.GetGrainCount(new string(clrName.ToCharArray()));
            counters[worker] = counter;
            for (var i = 0; i < instancesPerWorker; i++)
            {
                instruments.IncrementGrainCounts("orders", counter);
            }
        });

        var entry = Assert.Single(instruments.GrainCounts);
        Assert.Equal(clrName, entry.Key);
        Assert.All(counters, counter => Assert.Same(entry.Value, counter));
        Assert.Equal(counters.Length * instancesPerWorker, entry.Value.Value);
        var statistics = new GrainCountStatistics(instruments);
        Assert.Equal(new KeyValuePair<string, long>(clrName, counters.Length * instancesPerWorker), Assert.Single(statistics.GetSimpleGrainStatistics()));

        Parallel.For(0, counters.Length, worker =>
        {
            for (var i = 0; i < instancesPerWorker; i++)
            {
                instruments.DecrementGrainCounts("orders", counters[worker]);
            }
        });

        Assert.Equal(0, entry.Value.Value);
        Assert.Empty(statistics.GetSimpleGrainStatistics());
        Assert.Same(entry.Value, instruments.GetGrainCount(clrName));
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Theory, TestCategory("BVT")]
    [InlineData(true)]
    [InlineData(false)]
    public void WarmGrainRecording_AllocatesZeroWithAndWithoutLiveCallbacks(bool enabled)
    {
        using var fixture = new GrainMetricFixture();
        var first = new GrainTypeMetrics(GrainType.Create("warm-orders")).GrainTypeTagValue;
        var second = new GrainTypeMetrics(GrainType.Create("warm-invoices")).GrainTypeTagValue;
        var target = new string("Example.WarmSystemTarget".ToCharArray());
        const string firstClr = "Example.OrderGrain";
        const string secondClr = "Example.InvoiceGrain";
        var firstCount = fixture.Instruments.GetGrainCount(firstClr);
        var secondCount = fixture.Instruments.GetGrainCount(secondClr);
        fixture.Instruments.IncrementGrainCounts(first, firstCount);
        fixture.Instruments.IncrementGrainCounts(first, firstCount);
        fixture.Instruments.IncrementGrainCounts(second, secondCount);
        var positives = new int[3];
        var negatives = new int[3];
        var mismatches = 0;
        using var listener = fixture.Listen((instrument, value, tags, _) =>
        {
            var isSystemTarget = instrument.Name == InstrumentNames.SYSTEM_TARGET_COUNTS;
            var index = isSystemTarget ? 2 : tags.Length == 1 && ReferenceEquals(tags[0].Value, first) ? 0 : 1;
            if (instrument is not UpDownCounter<int> || tags.Length != 1
                || tags[0].Key != (isSystemTarget ? "type" : "grain_type")
                || !ReferenceEquals(tags[0].Value, index == 0 ? first : index == 1 ? second : target))
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
            fixture.Instruments.IncrementGrainCounts(first, firstCount);
            fixture.Instruments.DecrementGrainCounts(first, firstCount);
            fixture.Instruments.IncrementGrainCounts(second, secondCount);
            fixture.Instruments.DecrementGrainCounts(second, secondCount);
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
        Assert.Equal(2, fixture.Instruments.GrainCounts.Count);
        Assert.Equal(2, fixture.Instruments.GrainCounts[firstClr].Value);
        Assert.Equal(1, fixture.Instruments.GrainCounts[secondClr].Value);
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
        fixture.Instruments.IncrementGrainCounts(first, firstCount);
        Assert.Equal(3, fixture.Instruments.GrainCounts[firstClr].Value);
        Assert.Equal(1, fixture.Instruments.GrainCounts[secondClr].Value);
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
        public List<Instrument> PublishedInstruments { get; } = new();

        public GrainMetricFixture()
        {
            _provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
            var orleans = new OrleansInstruments(_provider.GetRequiredService<IMeterFactory>());
            _meter = orleans.Meter;
            Instruments = new GrainInstruments(orleans);
        }

        public MeterListener Listen(MeasurementCallback<int> callback, bool enable = true)
        {
            var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, owner) =>
            {
                if (ReferenceEquals(instrument.Meter, _meter))
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
