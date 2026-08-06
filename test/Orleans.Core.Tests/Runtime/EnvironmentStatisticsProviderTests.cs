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
        Instrument availableMemoryInstrument = null!;
        Instrument maximumAvailableMemoryInstrument = null!;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == InstrumentNames.RUNTIME_MEMORY_AVAILABLE_MEMORY_MB)
            {
                availableMemoryInstrument = instrument;
            }
            else if (instrument.Name == InstrumentNames.RUNTIME_MEMORY_TOTAL_PHYSICAL_MEMORY_MB)
            {
                maximumAvailableMemoryInstrument = instrument;
            }
        };

        listener.Start();

        using var provider = new EnvironmentStatisticsProvider();

        Assert.NotNull(availableMemoryInstrument);
        Assert.NotNull(maximumAvailableMemoryInstrument);
        Assert.IsType<ObservableGauge<long>>(availableMemoryInstrument);
        Assert.IsType<ObservableGauge<long>>(maximumAvailableMemoryInstrument);
    }
}
