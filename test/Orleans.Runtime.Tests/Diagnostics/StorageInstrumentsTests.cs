using System;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Orleans.Runtime;
using Xunit;

namespace Tester.Diagnostics;

public class StorageInstrumentsTests
{
    [Fact, TestCategory("BVT")]
    public void StorageInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new StorageInstruments(new OrleansInstruments(meterFactory));
        using var readLatencyCollector = new MetricCollector<double>(meterFactory, "Microsoft.Orleans", InstrumentNames.STORAGE_READ_LATENCY);
        using var writeLatencyCollector = new MetricCollector<double>(meterFactory, "Microsoft.Orleans", InstrumentNames.STORAGE_WRITE_LATENCY);
        using var clearLatencyCollector = new MetricCollector<double>(meterFactory, "Microsoft.Orleans", InstrumentNames.STORAGE_CLEAR_LATENCY);
        using var readErrorCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.STORAGE_READ_ERRORS);
        using var writeErrorCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.STORAGE_WRITE_ERRORS);
        using var clearErrorCollector = new MetricCollector<int>(meterFactory, "Microsoft.Orleans", InstrumentNames.STORAGE_CLEAR_ERRORS);

        instruments.OnStorageRead(TimeSpan.FromMilliseconds(12), "provider", "state", "stateType");
        instruments.OnStorageWrite(TimeSpan.FromMilliseconds(34), "provider", "state", "stateType");
        instruments.OnStorageDelete(TimeSpan.FromMilliseconds(56), "provider", "state", "stateType");
        instruments.OnStorageReadError("provider", "state", "stateType");
        instruments.OnStorageWriteError("provider", "state", "stateType");
        instruments.OnStorageDeleteError("provider", "state", "stateType");

        Assert.Equal(12, Assert.Single(readLatencyCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(34, Assert.Single(writeLatencyCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(56, Assert.Single(clearLatencyCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(readErrorCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(writeErrorCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(clearErrorCollector.GetMeasurementSnapshot()).Value);
    }
}