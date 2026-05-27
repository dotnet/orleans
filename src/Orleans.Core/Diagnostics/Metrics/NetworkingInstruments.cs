using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Orleans.Messaging;

#nullable disable
namespace Orleans.Runtime;

internal class NetworkingInstruments(OrleansInstruments instruments)
{
    private readonly Counter<int> _closedSocketsCounter = instruments.Meter.CreateCounter<int>(InstrumentNames.NETWORKING_SOCKETS_CLOSED);
    private readonly Counter<int> _openedSocketsCounter = instruments.Meter.CreateCounter<int>(InstrumentNames.NETWORKING_SOCKETS_OPENED);

    internal void OnOpenedSocket(ConnectionDirection direction)
    {
        _openedSocketsCounter.Add(1, new KeyValuePair<string, object>("Direction", direction.ToString()));
    }

    internal void OnClosedSocket(ConnectionDirection direction)
    {
        _closedSocketsCounter.Add(1, new KeyValuePair<string, object>("Direction", direction.ToString()));
    }
}
