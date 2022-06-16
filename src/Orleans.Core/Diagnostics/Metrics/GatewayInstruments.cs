using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

internal static class GatewayInstruments
{
    internal static readonly Counter<int> GatewaySent = Instruments.Meter.CreateCounter<int>(StatisticNames.GATEWAY_SENT);
    internal static readonly Counter<int> GatewayReceived = Instruments.Meter.CreateCounter<int>(StatisticNames.GATEWAY_RECEIVED);
    internal static readonly Counter<int> GatewayLoadShedding = Instruments.Meter.CreateCounter<int>(StatisticNames.GATEWAY_LOAD_SHEDDING);
}
