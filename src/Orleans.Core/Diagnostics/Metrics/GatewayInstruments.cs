using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class GatewayInstruments
{
    internal static readonly Counter<int> GatewaySent = Instruments.Meter.CreateCounter<int>(InstrumentNames.GATEWAY_SENT);
    internal static readonly Counter<int> GatewayReceived = Instruments.Meter.CreateCounter<int>(InstrumentNames.GATEWAY_RECEIVED);
    internal static readonly Counter<int> GatewayLoadShedding = Instruments.Meter.CreateCounter<int>(InstrumentNames.GATEWAY_LOAD_SHEDDING);
}
