using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
#if NET10_0_OR_GREATER
using OpenTelemetry;
using OpenTelemetry.Metrics;
#endif
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class ApplicationRequestInstrumentsTests
{
#if NET10_0_OR_GREATER
    private static readonly double[] ExpectedBoundaries =
    [
        0.1, 0.25, 0.5, 0.75,
        1, 2, 4, 6, 8, 10, 50, 100,
        200, 400, 800, 1_000, 1_500, 2_000,
        5_000, 10_000, 15_000, 30_000
    ];
#endif

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void TimedOutAndCanceledCounters_CarryGrainTypeTag()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new ApplicationRequestInstruments(new OrleansInstruments(meterFactory));
        using var timedOutCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", InstrumentNames.APP_REQUESTS_TIMED_OUT);
        using var canceledCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", InstrumentNames.APP_REQUESTS_CANCELED);

        instruments.OnAppRequestsTimedOut("mygrain");
        instruments.OnAppRequestsCanceled("othergrain");

        var timedOut = Assert.Single(timedOutCollector.GetMeasurementSnapshot());
        Assert.Equal(1, timedOut.Value);
        Assert.Equal("mygrain", Assert.Contains("grain_type", timedOut.Tags));

        var canceled = Assert.Single(canceledCollector.GetMeasurementSnapshot());
        Assert.Equal(1, canceled.Value);
        Assert.Equal("othergrain", Assert.Contains("grain_type", canceled.Tags));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void RequestLatencyHistogram_RecordsFractionalMilliseconds()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new ApplicationRequestInstruments(new OrleansInstruments(meterFactory));

        Assert.False(instruments.AppRequestsLatencyEnabled);

        using var collector = new MetricCollector<double>(meterFactory, "Microsoft.Orleans", InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM);

        Assert.True(instruments.AppRequestsLatencyEnabled);

        instruments.OnAppRequestsEnd(0.125);

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(0.125, measurement.Value);
        Assert.Empty(measurement.Tags);
    }

#if NET10_0_OR_GREATER
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void RequestLatencyHistogram_ProvidesRequestSpecificBoundaryAdvice()
    {
        using var listener = new MeterListener();
        Instrument publishedInstrument = null!;
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Name == InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM)
            {
                publishedInstrument = instrument;
            }
        };
        listener.Start();

        using var meter = new Meter($"ApplicationRequestInstrumentsTests.{Guid.NewGuid():N}");
        using var meterFactory = new FixedMeterFactory(meter);
        _ = new ApplicationRequestInstruments(new OrleansInstruments(meterFactory));

        var histogram = Assert.IsType<Histogram<double>>(publishedInstrument);
        Assert.Equal("ms", histogram.Unit);
        Assert.NotNull(histogram.Advice);
        Assert.Equal(ExpectedBoundaries, histogram.Advice!.HistogramBucketBoundaries);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void RequestLatencyHistogram_ExportsCountSumMinMaxAndAdvisedBuckets()
    {
        using var test = new HistogramExportTest();

        test.Instruments.OnAppRequestsEnd(0.05);
        test.Instruments.OnAppRequestsEnd(0.1);
        test.Instruments.OnAppRequestsEnd(0.2);
        test.Instruments.OnAppRequestsEnd(0.5);
        test.Instruments.OnAppRequestsEnd(1.5);
        test.Instruments.OnAppRequestsEnd(30_001);

        var snapshot = test.Collect();

        Assert.Equal(6, snapshot.Count);
        Assert.Equal(30_003.35, snapshot.Sum, 5);
        Assert.Equal(0.05, snapshot.Min);
        Assert.Equal(30_001, snapshot.Max);
        Assert.Equal([.. ExpectedBoundaries, double.PositiveInfinity], snapshot.Buckets.Select(static bucket => bucket.Bound));
        Assert.Equal([2L, 1, 1, 0, 0, 1], snapshot.Buckets.Take(6).Select(static bucket => bucket.Count));
        Assert.All(snapshot.Buckets.Skip(6).Take(16), static bucket => Assert.Equal(0, bucket.Count));
        Assert.Equal(1, snapshot.Buckets[^1].Count);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void RequestLatencyHistogram_ViewOverridesBoundaryAdvice()
    {
        using var test = new HistogramExportTest([1, 5]);

        test.Instruments.OnAppRequestsEnd(0.5);
        test.Instruments.OnAppRequestsEnd(2);
        test.Instruments.OnAppRequestsEnd(10);

        var snapshot = test.Collect();

        Assert.Equal([1, 5, double.PositiveInfinity], snapshot.Buckets.Select(static bucket => bucket.Bound));
        Assert.Equal([1L, 1, 1], snapshot.Buckets.Select(static bucket => bucket.Count));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void RequestLatencyHistogram_UsesReaderTemporality()
    {
        using var cumulative = new HistogramExportTest(temporality: MetricReaderTemporalityPreference.Cumulative);
        cumulative.Instruments.OnAppRequestsEnd(1);
        cumulative.Instruments.OnAppRequestsEnd(2);
        Assert.Equal(2, cumulative.Collect().Count);
        cumulative.Instruments.OnAppRequestsEnd(3);
        Assert.Equal(3, cumulative.Collect().Count);

        using var delta = new HistogramExportTest(temporality: MetricReaderTemporalityPreference.Delta);
        delta.Instruments.OnAppRequestsEnd(1);
        delta.Instruments.OnAppRequestsEnd(2);
        Assert.Equal(2, delta.Collect().Count);
        delta.Instruments.OnAppRequestsEnd(3);
        Assert.Equal(1, delta.Collect().Count);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void RequestLatencyHistogram_RecordsConcurrently()
    {
        using var test = new HistogramExportTest();

        Parallel.For(0, 10_000, i => test.Instruments.OnAppRequestsEnd(i % 10));

        var snapshot = test.Collect();
        Assert.Equal(10_000, snapshot.Count);
        Assert.Equal(45_000, snapshot.Sum);
    }

    private sealed class HistogramExportTest : IDisposable
    {
        private readonly Meter _meter;
        private readonly FixedMeterFactory _meterFactory;
        private readonly HistogramExporter _exporter = new();
        private readonly MeterProvider _provider;

        public HistogramExportTest(
            double[]? boundaries = null,
            MetricReaderTemporalityPreference temporality = MetricReaderTemporalityPreference.Cumulative)
        {
            var meterName = $"ApplicationRequestInstrumentsTests.{Guid.NewGuid():N}";
            var builder = Sdk.CreateMeterProviderBuilder().AddMeter(meterName);
            if (boundaries is not null)
            {
                builder.AddView(
                    InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM,
                    new ExplicitBucketHistogramConfiguration { Boundaries = boundaries, RecordMinMax = true });
            }

            _provider = builder.AddReader(new BaseExportingMetricReader(_exporter) { TemporalityPreference = temporality }).Build();
            _meter = new Meter(meterName);
            _meterFactory = new FixedMeterFactory(_meter);
            Instruments = new ApplicationRequestInstruments(new OrleansInstruments(_meterFactory));
        }

        public ApplicationRequestInstruments Instruments { get; }

        public HistogramSnapshot Collect()
        {
            Assert.True(_provider.ForceFlush());
            return _exporter.TakeSnapshot();
        }

        public void Dispose()
        {
            _provider.Dispose();
            _meterFactory.Dispose();
            _meter.Dispose();
        }
    }

    private sealed class FixedMeterFactory(Meter meter) : IMeterFactory
    {
        public Meter Create(MeterOptions options) => meter;

        public void Dispose()
        {
        }
    }

    private sealed class HistogramExporter : BaseExporter<Metric>
    {
        private readonly List<HistogramSnapshot> _snapshots = [];

        public HistogramSnapshot TakeSnapshot()
        {
            var result = Assert.Single(_snapshots);
            _snapshots.Clear();
            return result;
        }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.Name != InstrumentNames.APP_REQUESTS_LATENCY_HISTOGRAM)
                {
                    continue;
                }

                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    Assert.True(point.TryGetHistogramMinMaxValues(out var min, out var max));
                    var buckets = new List<HistogramBucketSnapshot>();
                    foreach (var bucket in point.GetHistogramBuckets())
                    {
                        buckets.Add(new(bucket.ExplicitBound, bucket.BucketCount));
                    }

                    _snapshots.Add(
                        new(
                            point.GetHistogramCount(),
                            point.GetHistogramSum(),
                            min,
                            max,
                            buckets));
                }
            }

            return ExportResult.Success;
        }
    }

    private sealed record HistogramSnapshot(
        long Count,
        double Sum,
        double Min,
        double Max,
        IReadOnlyList<HistogramBucketSnapshot> Buckets);

    private sealed record HistogramBucketSnapshot(double Bound, long Count);
#endif
}
