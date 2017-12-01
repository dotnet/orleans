namespace Orleans.Runtime
{
    internal class NetworkingStatisticsGroup
    {
        // SILO Sockets
        // sending
        private static CounterStatistic closedSiloSendingSockets;
        private static CounterStatistic openedSiloSendingSockets;
        // receiving
        private static CounterStatistic closedSiloReceivingSockets;
        private static CounterStatistic openedSiloReceivingSockets;

        // Gateway SOCKETS
        // Client to Gateway and Gateway to Client use the same Duplex socket for send and receive, so we count them once.
        private static CounterStatistic closedGatewayToClientDuplexSockets;
        private static CounterStatistic openedGatewayToClientDuplexSockets;

        // CLIENT SOCKETS
        // duplex
        private static CounterStatistic closedClientToGatewayDuplexSockets;
        private static CounterStatistic openedClientToGatewayDuplexSockets;

        private static bool isSilo;

        internal static void Init(bool silo)
        {
            isSilo = silo;
            if (isSilo)
            {
                closedSiloSendingSockets = CounterStatistic.FindOrCreate(StatisticNames.NETWORKING_SOCKETS_SILO_SENDING_CLOSED);
                openedSiloSendingSockets = CounterStatistic.FindOrCreate(StatisticNames.NETWORKING_SOCKETS_SILO_SENDING_OPENED);
                closedSiloReceivingSockets = CounterStatistic.FindOrCreate(StatisticNames.NETWORKING_SOCKETS_SILO_RECEIVING_CLOSED);
                openedSiloReceivingSockets = CounterStatistic.FindOrCreate(StatisticNames.NETWORKING_SOCKETS_SILO_RECEIVING_OPENED);
                closedGatewayToClientDuplexSockets = CounterStatistic.FindOrCreate(StatisticNames.NETWORKING_SOCKETS_GATEWAYTOCLIENT_DUPLEX_CLOSED);
                openedGatewayToClientDuplexSockets = CounterStatistic.FindOrCreate(StatisticNames.NETWORKING_SOCKETS_GATEWAYTOCLIENT_DUPLEX_OPENED);
            }
            else
            {
                closedClientToGatewayDuplexSockets = CounterStatistic.FindOrCreate(StatisticNames.NETWORKING_SOCKETS_CLIENTTOGATEWAY_DUPLEX_CLOSED );
                openedClientToGatewayDuplexSockets = CounterStatistic.FindOrCreate(StatisticNames.NETWORKING_SOCKETS_CLIENTTOGATEWAY_DUPLEX_OPENED);
            }
        }

        internal static void OnOpenedSendingSocket()
        {
            if (isSilo)
            {
                openedSiloSendingSockets.Increment();
            }
        }

        internal static void OnClosedSendingSocket()
        {
            if (isSilo)
            {
                closedSiloSendingSockets.Increment();
            }
        }

        internal static void OnOpenedReceiveSocket()
        {
            if (isSilo)
            {
                openedSiloReceivingSockets.Increment();
            }
        }

        internal static void OnClosedReceivingSocket()
        {
            if (isSilo)
            {
                closedSiloReceivingSockets.Increment();
            }
        }

        internal static void OnOpenedGatewayDuplexSocket()
        {
            if (isSilo)
            {
                openedGatewayToClientDuplexSockets.Increment();
            }
            else
            {
                openedClientToGatewayDuplexSockets.Increment();
            }
        }

        internal static void OnClosedGatewayDuplexSocket()
        {
            if (isSilo)
            {
                closedGatewayToClientDuplexSockets.Increment();
            }
            else
            {
                closedClientToGatewayDuplexSockets.Increment();
            }
        }
    }
}
