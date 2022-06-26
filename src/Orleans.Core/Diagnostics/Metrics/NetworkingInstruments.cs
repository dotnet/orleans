using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Orleans.Messaging;

namespace Orleans.Runtime;

internal static class NetworkingInstruments
{
    internal static Counter<int> ClosedSocketsCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.NETWORKING_SOCKETS_CLOSED);
    internal static Counter<int> OpenedSocketsCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.NETWORKING_SOCKETS_OPENED);

    internal static void OnOpenedSocket(ConnectionDirection direction)
    {
        OpenedSocketsCounter.Add(1, new KeyValuePair<string, object>("Direction", direction.ToString()));
    }

    internal static void OnClosedSocket(ConnectionDirection direction)
    {
        ClosedSocketsCounter.Add(1, new KeyValuePair<string, object>("Direction", direction.ToString()));
    }
}
