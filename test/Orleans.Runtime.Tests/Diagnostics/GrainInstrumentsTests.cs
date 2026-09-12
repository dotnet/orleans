using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;
using System;
using System.Collections.Generic;
using System.Linq;

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
        using var grainCountsCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.GRAIN_COUNTS);
        using var systemTargetCountsCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.SYSTEM_TARGET_COUNTS);

        instruments.IncrementGrainCounts("grain", "Example.Grain");
        instruments.DecrementGrainCounts("grain", "Example.Grain");
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
        fixture.Instruments.IncrementGrainCounts("untouched", "Example.UnrelatedGrain");
        fixture.Instruments.IncrementGrainCounts("untouched", "Example.UnrelatedGrain");
        var observations = new List<GrainMeasurement>();
        using var listener = fixture.Listen((instrument, value, tags, _) => observations.Add(new(instrument, value, tags.ToArray())));

        fixture.Instruments.IncrementGrainCounts(first, "Example.OrderGrain");
        fixture.Instruments.IncrementGrainCounts(first, "Example.OrderGrain");
        fixture.Instruments.IncrementGrainCounts(second, "Example.InvoiceGrain");
        fixture.Instruments.DecrementGrainCounts(first, "Example.OrderGrain");
        fixture.Instruments.DecrementGrainCounts(second, "Example.InvoiceGrain");

        Assert.Collection(observations,
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", first),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", first),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", second),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, -1, "grain_type", first),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, -1, "grain_type", second));
        Assert.Equal(
            new[] { new KeyValuePair<string, int>("Example.InvoiceGrain", 0), new("Example.OrderGrain", 1), new("Example.UnrelatedGrain", 2) },
            fixture.Instruments.GrainCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [TestSuite("BVT"), TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void SystemTargetCounts_RetainTypeTagsWithoutAffectingGrainStatistics()
    {
        using var fixture = new GrainMetricFixture();
        var grain = new GrainTypeMetrics(GrainType.Create("interleaved-orders")).GrainTypeTagValue;
        var target = new string("Example.CatalogSystemTarget".ToCharArray());
        var observations = new List<GrainMeasurement>();
        using var listener = fixture.Listen((instrument, value, tags, _) => observations.Add(new(instrument, value, tags.ToArray())));

        fixture.Instruments.IncrementGrainCounts(grain, "Example.OrderGrain");
        fixture.Instruments.IncrementSystemTargetCounts(target);
        fixture.Instruments.DecrementGrainCounts(grain, "Example.OrderGrain");
        fixture.Instruments.DecrementSystemTargetCounts(target);

        Assert.Collection(observations,
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, 1, "grain_type", grain),
            item => AssertGrainMeasurement(item, InstrumentNames.SYSTEM_TARGET_COUNTS, 1, "type", target),
            item => AssertGrainMeasurement(item, InstrumentNames.GRAIN_COUNTS, -1, "grain_type", grain),
            item => AssertGrainMeasurement(item, InstrumentNames.SYSTEM_TARGET_COUNTS, -1, "type", target));
        var count = Assert.Single(fixture.Instruments.GrainCounts);
        Assert.Equal("Example.OrderGrain", count.Key);
        Assert.Equal(0, count.Value);
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
        instruments.IncrementGrainCounts("orders", "Example.OrderGrain");
        instruments.IncrementGrainCounts("orders", "Example.OrderGrain");
        instruments.IncrementGrainCounts("invoices", "Example.InvoiceGrain");
        instruments.IncrementGrainCounts("zero", "Example.ZeroGrain");
        instruments.DecrementGrainCounts("zero", "Example.ZeroGrain");
        instruments.DecrementGrainCounts("negative", "Example.NegativeGrain");
        instruments.IncrementSystemTargetCounts("Example.CatalogSystemTarget");

        var snapshot = statistics.GetSimpleGrainStatistics().OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { new KeyValuePair<string, long>("Example.InvoiceGrain", 1L), new("Example.OrderGrain", 2L) },
            snapshot);
        Assert.Equal(4, instruments.GrainCounts.Count);
        Assert.Equal(0, instruments.GrainCounts["Example.ZeroGrain"]);
        Assert.Equal(-1, instruments.GrainCounts["Example.NegativeGrain"]);
        Assert.False(instruments.GrainCounts.ContainsKey("Example.MissingGrain"));

        instruments.DecrementGrainCounts("orders", "Example.OrderGrain");
        instruments.DecrementGrainCounts("orders", "Example.OrderGrain");
        Assert.Equal(new KeyValuePair<string, long>("Example.InvoiceGrain", 1L), Assert.Single(statistics.GetSimpleGrainStatistics()));
        Assert.Equal(0, instruments.GrainCounts["Example.OrderGrain"]);
        Assert.Equal(-1, instruments.GrainCounts["Example.NegativeGrain"]);
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

        first.Instruments.IncrementGrainCounts("first-silo-type", clrName);
        second.Instruments.IncrementGrainCounts("second-silo-type", clrName);
        second.Instruments.IncrementGrainCounts("second-silo-type", clrName);
        Assert.Equal(new KeyValuePair<string, long>(clrName, 1L), Assert.Single(firstStatistics.GetSimpleGrainStatistics()));
        Assert.Equal(new KeyValuePair<string, long>(clrName, 2L), Assert.Single(secondStatistics.GetSimpleGrainStatistics()));
        Assert.NotSame(first.Instruments.GrainCounts, second.Instruments.GrainCounts);

        first.Instruments.DecrementGrainCounts("first-silo-type", clrName);
        Assert.Empty(firstStatistics.GetSimpleGrainStatistics());
        Assert.Equal(0, first.Instruments.GrainCounts[clrName]);
        Assert.Equal(new KeyValuePair<string, long>(clrName, 2L), Assert.Single(secondStatistics.GetSimpleGrainStatistics()));
        second.Instruments.DecrementGrainCounts("second-silo-type", clrName);
        Assert.Equal(new KeyValuePair<string, long>(clrName, 1L), Assert.Single(secondStatistics.GetSimpleGrainStatistics()));
        Assert.Empty(firstStatistics.GetSimpleGrainStatistics());
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
        fixture.Instruments.IncrementGrainCounts(first, firstClr);
        fixture.Instruments.IncrementGrainCounts(first, firstClr);
        fixture.Instruments.IncrementGrainCounts(second, secondClr);
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
            fixture.Instruments.IncrementGrainCounts(first, firstClr);
            fixture.Instruments.DecrementGrainCounts(first, firstClr);
            fixture.Instruments.IncrementGrainCounts(second, secondClr);
            fixture.Instruments.DecrementGrainCounts(second, secondClr);
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
        Assert.Equal(2, fixture.Instruments.GrainCounts[firstClr]);
        Assert.Equal(1, fixture.Instruments.GrainCounts[secondClr]);
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
        fixture.Instruments.IncrementGrainCounts(first, firstClr);
        Assert.Equal(3, fixture.Instruments.GrainCounts[firstClr]);
        Assert.Equal(1, fixture.Instruments.GrainCounts[secondClr]);
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
