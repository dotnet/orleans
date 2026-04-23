using System.Diagnostics.Metrics;
using Orleans.Runtime;
using Orleans.Statistics;
using Xunit;

namespace UnitTests.Runtime;

public class EnvironmentStatisticsProviderTests
{
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public void RuntimeMemoryMetrics_AreObservableGauges()
    {
        Instrument availableMemoryMetric = null!;
        Instrument maximumAvailableMemoryMetric = null!;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == InstrumentNames.RUNTIME_MEMORY_AVAILABLE_MEMORY_MB)
            {
                availableMemoryMetric = instrument;
                meterListener.EnableMeasurementEvents(instrument);
            }
            else if (instrument.Name == InstrumentNames.RUNTIME_MEMORY_TOTAL_PHYSICAL_MEMORY_MB)
            {
                maximumAvailableMemoryMetric = instrument;
                meterListener.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        listener.Start();

        using var provider = new EnvironmentStatisticsProvider();
        listener.RecordObservableInstruments();

        Assert.NotNull(availableMemoryMetric);
        Assert.NotNull(maximumAvailableMemoryMetric);
        Assert.IsType<ObservableGauge<long>>(availableMemoryMetric);
        Assert.IsType<ObservableGauge<long>>(maximumAvailableMemoryMetric);
    }
}
