using System;
using Orleans.Messaging;

namespace Orleans.Runtime
{
    internal class NetworkingStatisticsGroup
    {
        private static CounterStatistic[] closedSockets;
        private static CounterStatistic[] openedSockets;

        internal static void Init()
        {
            closedSockets ??= new CounterStatistic[Enum.GetValues(typeof(ConnectionDirection)).Length];
            openedSockets ??= new CounterStatistic[Enum.GetValues(typeof(ConnectionDirection)).Length];

            openedSockets[(int)ConnectionDirection.SiloToSilo] = CounterStatistic.FindOrCreate(StatisticNames.NETWORKING_SOCKETS_SILO_OPENED);
            closedSockets[(int)ConnectionDirection.SiloToSilo] = CounterStatistic.FindOrCreate(StatisticNames.NETWORKING_SOCKETS_SILO_CLOSED);

            openedSockets[(int)ConnectionDirection.GatewayToClient] = CounterStatistic.FindOrCreate(StatisticNames.NETWORKING_SOCKETS_GATEWAYTOCLIENT_OPENED);
            closedSockets[(int)ConnectionDirection.GatewayToClient] = CounterStatistic.FindOrCreate(StatisticNames.NETWORKING_SOCKETS_GATEWAYTOCLIENT_CLOSED);

            openedSockets[(int)ConnectionDirection.ClientToGateway] = CounterStatistic.FindOrCreate(StatisticNames.NETWORKING_SOCKETS_CLIENTTOGATEWAY_OPENED);
            closedSockets[(int)ConnectionDirection.ClientToGateway] = CounterStatistic.FindOrCreate(StatisticNames.NETWORKING_SOCKETS_CLIENTTOGATEWAY_CLOSED);
        }

        internal static void OnOpenedSocket(ConnectionDirection direction)
        {
            openedSockets[(int)direction].Increment();
        }

        internal static void OnClosedSocket(ConnectionDirection direction)
        {
            closedSockets[(int)direction].Increment();
        }
    }
}
