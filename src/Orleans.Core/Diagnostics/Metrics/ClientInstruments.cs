using System;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

public static class ClientInstruments
{
    internal static ObservableGauge<int> ConnectedGatewayCount;
    internal static void RegisterConnectedGatewayCountObserve(Func<int> observeValue)
    {
        ConnectedGatewayCount = Instruments.Meter.CreateObservableGauge(InstrumentNames.CLIENT_CONNECTED_GATEWAY_COUNT, observeValue);
    }
}
