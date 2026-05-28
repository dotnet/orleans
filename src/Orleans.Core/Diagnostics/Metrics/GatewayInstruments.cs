using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal sealed class GatewayInstruments(OrleansInstruments instruments)
{
    private readonly Counter<int> _gatewaySent = instruments.Meter.CreateCounter<int>(InstrumentNames.GATEWAY_SENT);
    private readonly Counter<int> _gatewayReceived = instruments.Meter.CreateCounter<int>(InstrumentNames.GATEWAY_RECEIVED);
    private readonly Counter<int> _gatewayLoadShedding = instruments.Meter.CreateCounter<int>(InstrumentNames.GATEWAY_LOAD_SHEDDING);

    internal void OnGatewaySent()
    {
        _gatewaySent.Add(1);
    }

    internal void OnGatewayReceived()
    {
        _gatewayReceived.Add(1);
    }

    internal void OnGatewayLoadShedding()
    {
        _gatewayLoadShedding.Add(1);
    }
}
