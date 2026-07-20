using System;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal sealed class ClientInstruments(OrleansInstruments instruments)
{
    private ObservableGauge<int>? _connectedGatewayCount;
    internal void RegisterConnectedGatewayCountObserve(Func<int> observeValue)
    {
        _connectedGatewayCount = instruments.Meter.CreateObservableGauge(InstrumentNames.CLIENT_CONNECTED_GATEWAY_COUNT, observeValue);
    }
}
