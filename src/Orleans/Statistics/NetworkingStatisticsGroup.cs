/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿namespace Orleans.Runtime
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
