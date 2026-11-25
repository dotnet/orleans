using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using Orleans.Concurrency;
using Orleans.Dashboard.Core;
using Orleans.Dashboard.Implementation;
using Orleans.Dashboard.Model;
using Orleans.Runtime;
using Xunit;

namespace UnitTests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dashboard")]
public class DashboardTelemetryExporterTests
{
    [Fact]
    public void CumulativeExports_CombineTaggedPoints_AndComputeDeltasBetweenExports()
    {
        using var fixture = new ExportFixture();
        var counter = fixture.Meter.CreateCounter<long>("messages");
        var clients = fixture.Meter.CreateUpDownCounter<int>("clients");
        var doubleCounter = fixture.Meter.CreateCounter<double>("double-counter");
        var doubleUpDown = fixture.Meter.CreateUpDownCounter<double>("double-up-down");
        var histogram = fixture.Meter.CreateHistogram<int>("bytes");
        counter.Add(10, new KeyValuePair<string, object?>("direction", "request"));
        counter.Add(3, new KeyValuePair<string, object?>("direction", "response"));
        clients.Add(2);
        doubleCounter.Add(1.5);
        doubleUpDown.Add(2.5);
        histogram.Record(100, new KeyValuePair<string, object?>("direction", "request"));
        histogram.Record(50, new KeyValuePair<string, object?>("direction", "response"));

        fixture.Collect();
        fixture.AssertCounter("messages", "13", "13");
        fixture.AssertCounter("clients", "2", "2");
        fixture.AssertCounter("double-counter", "1.5", "1.5");
        fixture.AssertCounter("double-up-down", "2.5", "2.5");
        fixture.AssertCounter("bytes", "150", "150");

        counter.Add(5, new KeyValuePair<string, object?>("direction", "request"));
        counter.Add(7, new KeyValuePair<string, object?>("direction", "response"));
        clients.Add(-1);
        doubleCounter.Add(0.5);
        doubleUpDown.Add(-1.5);
        histogram.Record(25);
        fixture.Collect();
        fixture.AssertCounter("messages", "25", "12");
        fixture.AssertCounter("clients", "1", "-1");
        fixture.AssertCounter("double-counter", "2", "0.5");
        fixture.AssertCounter("double-up-down", "1", "-1.5");
        fixture.AssertCounter("bytes", "175", "25");

        fixture.Collect();
        fixture.AssertCounter("messages", "25", "0");
        fixture.AssertCounter("clients", "1", "0");
        fixture.AssertCounter("bytes", "175", "0");
    }

    [Fact]
    public void Gauges_ExportCurrentTotalsAndChanges()
    {
        using var fixture = new ExportFixture();
        var count = 4L;
        var value = 1.5;
        fixture.Meter.CreateObservableGauge("long-gauge", () => count);
        fixture.Meter.CreateObservableGauge("double-gauge", () => value);
        fixture.Collect();
        fixture.AssertCounter("long-gauge", "4", "4");
        fixture.AssertCounter("double-gauge", "1.5", "1.5");
        count = 2;
        value = 2.25;
        fixture.Collect();
        fixture.AssertCounter("long-gauge", "2", "-2");
        fixture.AssertCounter("double-gauge", "2.25", "0.75");
    }

    private sealed class ExportFixture : IDisposable, ISiloGrainClient, ILocalSiloDetails
    {
        private readonly MeterProvider _provider;
        private readonly CounterServiceProxy _service;

        public ExportFixture()
        {
            Meter = new Meter($"dashboard-exporter-test-{Guid.NewGuid()}");
            var service = DispatchProxy.Create<ISiloGrainService, CounterServiceProxy>();
            _service = (CounterServiceProxy)service;
            var exporter = new DashboardTelemetryExporter(this, this, NullLogger<DashboardTelemetryExporter>.Instance);
            _provider = Sdk.CreateMeterProviderBuilder().AddMeter(Meter.Name).AddReader(new BaseExportingMetricReader(exporter)
            {
                TemporalityPreference = MetricReaderTemporalityPreference.Cumulative,
            }).Build();
        }

        public Meter Meter { get; }
        public string Name => "test-silo";
        public string ClusterId => "test-cluster";
        public string DnsHostName => "localhost";
        public SiloAddress SiloAddress { get; } = SiloAddress.New(IPAddress.Loopback, 11111, 1);
        public SiloAddress GatewayAddress => SiloAddress;
        public ISiloGrainService GrainService(SiloAddress destination) => (ISiloGrainService)_service;

        public void Collect() => Assert.True(_provider.ForceFlush());

        public void AssertCounter(string name, string value, string delta)
        {
            var counter = Assert.Single(_service.Counters, counter => counter.Name == name);
            Assert.Equal(value, counter.Value);
            Assert.Equal(delta, counter.Delta);
        }

        public void Dispose()
        {
            _provider.Dispose();
            Meter.Dispose();
        }
    }

    private class CounterServiceProxy : DispatchProxy
    {
        public StatCounter[] Counters { get; private set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(ISiloGrainService.ReportCounters)) throw new NotSupportedException(targetMethod.Name);
            Counters = ((Immutable<StatCounter[]>)args![0]!).Value;
            return Task.CompletedTask;
        }
    }
}
