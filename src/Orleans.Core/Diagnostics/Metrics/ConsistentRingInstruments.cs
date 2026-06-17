using System;
using System.Diagnostics.Metrics;

#nullable disable
namespace Orleans.Runtime;

internal sealed class ConsistentRingInstruments(OrleansInstruments instruments)
{
    private ObservableGauge<int> _ringSize;
    private ObservableGauge<float> _myRangeRingPercentage;
    private ObservableGauge<float> _averageRingPercentage;

    internal void RegisterRingSizeObserve(Func<int> observeValue)
    {
        _ringSize = instruments.Meter.CreateObservableGauge(InstrumentNames.CONSISTENTRING_SIZE, observeValue);
    }

    internal void RegisterMyRangeRingPercentageObserve(Func<float> observeValue)
    {
        _myRangeRingPercentage = instruments.Meter.CreateObservableGauge(InstrumentNames.CONSISTENTRING_LOCAL_SIZE_PERCENTAGE, observeValue);
    }

    internal void RegisterAverageRingPercentageObserve(Func<float> observeValue)
    {
        _averageRingPercentage = instruments.Meter.CreateObservableGauge(InstrumentNames.CONSISTENTRING_AVERAGE_SIZE_PERCENTAGE, observeValue);
    }
}
