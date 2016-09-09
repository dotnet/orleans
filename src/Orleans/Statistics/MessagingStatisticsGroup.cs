using System;
using System.Collections.Concurrent;
using Orleans.Messaging;

namespace Orleans.Runtime
{
    internal class MessagingStatisticsGroup
    {
        internal class PerSocketDirectionStats
        {
            private readonly AverageValueStatistic averageBatchSize;
            private readonly HistogramValueStatistic batchSizeBytesHistogram;

            internal PerSocketDirectionStats(bool sendOrReceive, SocketDirection direction)
            {
                StatisticNameFormat batchSizeStatName = sendOrReceive ? StatisticNames.MESSAGING_SENT_BATCH_SIZE_PER_SOCKET_DIRECTION : StatisticNames.MESSAGING_RECEIVED_BATCH_SIZE_PER_SOCKET_DIRECTION;
                StatisticNameFormat batchHistogramStatName = sendOrReceive ? StatisticNames.MESSAGING_SENT_BATCH_SIZE_BYTES_HISTOGRAM_PER_SOCKET_DIRECTION : StatisticNames.MESSAGING_RECEIVED_BATCH_SIZE_BYTES_HISTOGRAM_PER_SOCKET_DIRECTION;

                averageBatchSize = AverageValueStatistic.FindOrCreate(new StatisticName(batchSizeStatName, Enum.GetName(typeof(SocketDirection), direction)));
                batchSizeBytesHistogram = ExponentialHistogramValueStatistic.Create_ExponentialHistogram(
                    new StatisticName(batchHistogramStatName, Enum.GetName(typeof(SocketDirection), direction)), 
                    NUM_MSG_SIZE_HISTOGRAM_CATEGORIES);
            }

            internal void OnMessage(int numMsgsInBatch, int totalBytes)
            {
                averageBatchSize.AddValue(numMsgsInBatch);
                batchSizeBytesHistogram.AddData(totalBytes);
            }
        }

        internal static CounterStatistic MessagesSentTotal;
        internal static CounterStatistic[] MessagesSentPerDirection;
        internal static CounterStatistic TotalBytesSent;
        internal static CounterStatistic HeaderBytesSent;

        internal static CounterStatistic MessagesReceived;
        internal static CounterStatistic[] MessagesReceivedPerDirection;
        private static CounterStatistic totalBytesReceived;
        private static CounterStatistic headerBytesReceived;

        internal static CounterStatistic LocalMessagesSent;

        internal static CounterStatistic[] FailedSentMessages;
        internal static CounterStatistic[] DroppedSentMessages;
        internal static CounterStatistic[] RejectedMessages;
        internal static CounterStatistic[] ReroutedMessages;

        private static CounterStatistic expiredAtSendCounter;
        private static CounterStatistic expiredAtReceiveCounter;
        private static CounterStatistic expiredAtDispatchCounter;
        private static CounterStatistic expiredAtInvokeCounter;
        private static CounterStatistic expiredAtRespondCounter;

        internal static CounterStatistic ConnectedClientCount;

        private static PerSocketDirectionStats[] perSocketDirectionStatsSend;
        private static PerSocketDirectionStats[] perSocketDirectionStatsReceive;
        private static ConcurrentDictionary<string, CounterStatistic> perSiloSendCounters;
        private static ConcurrentDictionary<string, CounterStatistic> perSiloReceiveCounters;
        private static ConcurrentDictionary<string, CounterStatistic> perSiloPingSendCounters;
        private static ConcurrentDictionary<string, CounterStatistic> perSiloPingReceiveCounters;
        private static ConcurrentDictionary<string, CounterStatistic> perSiloPingReplyReceivedCounters;
        private static ConcurrentDictionary<string, CounterStatistic> perSiloPingReplyMissedCounters;
                
        internal enum Phase
        {
            Send,
            Receive,
            Dispatch,
            Invoke,
            Respond,
        }

        // Histogram of sent  message size, starting from 0 in multiples of 2
        // (1=2^0, 2=2^2, ... , 256=2^8, 512=2^9, 1024==2^10, ... , up to ... 2^30=1GB)
        private static HistogramValueStatistic sentMsgSizeHistogram;
        private static HistogramValueStatistic receiveMsgSizeHistogram;
        private const int NUM_MSG_SIZE_HISTOGRAM_CATEGORIES = 31;

        internal static void Init(bool silo)
        {
            if (silo)
            {
                LocalMessagesSent = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_SENT_LOCALMESSAGES);
                ConnectedClientCount = CounterStatistic.FindOrCreate(StatisticNames.GATEWAY_CONNECTED_CLIENTS, false);
            }

            MessagesSentTotal = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_SENT_MESSAGES_TOTAL);
            MessagesSentPerDirection = new CounterStatistic[Enum.GetValues(typeof(Message.Directions)).Length];
            foreach (var direction in Enum.GetValues(typeof(Message.Directions)))
            {
                MessagesSentPerDirection[(int)direction] = CounterStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.MESSAGING_SENT_MESSAGES_PER_DIRECTION, Enum.GetName(typeof(Message.Directions), direction)));
            }

            MessagesReceived = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_RECEIVED_MESSAGES_TOTAL);
            MessagesReceivedPerDirection = new CounterStatistic[Enum.GetValues(typeof(Message.Directions)).Length];
            foreach (var direction in Enum.GetValues(typeof(Message.Directions)))
            {
                MessagesReceivedPerDirection[(int)direction] = CounterStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.MESSAGING_RECEIVED_MESSAGES_PER_DIRECTION, Enum.GetName(typeof(Message.Directions), direction)));
            }

            TotalBytesSent = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_SENT_BYTES_TOTAL);
            totalBytesReceived = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_RECEIVED_BYTES_TOTAL);
            HeaderBytesSent = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_SENT_BYTES_HEADER);
            headerBytesReceived = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_RECEIVED_BYTES_HEADER);
            FailedSentMessages = new CounterStatistic[Enum.GetValues(typeof(Message.Directions)).Length];
            DroppedSentMessages = new CounterStatistic[Enum.GetValues(typeof(Message.Directions)).Length];
            RejectedMessages = new CounterStatistic[Enum.GetValues(typeof(Message.Directions)).Length];

            ReroutedMessages = new CounterStatistic[Enum.GetValues(typeof(Message.Directions)).Length];
            foreach (var direction in Enum.GetValues(typeof(Message.Directions)))
            {
                ReroutedMessages[(int)direction] = CounterStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.MESSAGING_REROUTED_PER_DIRECTION, Enum.GetName(typeof(Message.Directions), direction)));
            }

            sentMsgSizeHistogram = ExponentialHistogramValueStatistic.Create_ExponentialHistogram(StatisticNames.MESSAGING_SENT_MESSAGESIZEHISTOGRAM, NUM_MSG_SIZE_HISTOGRAM_CATEGORIES);
            receiveMsgSizeHistogram = ExponentialHistogramValueStatistic.Create_ExponentialHistogram(StatisticNames.MESSAGING_RECEIVED_MESSAGESIZEHISTOGRAM, NUM_MSG_SIZE_HISTOGRAM_CATEGORIES);

            expiredAtSendCounter = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_EXPIRED_ATSENDER);
            expiredAtReceiveCounter = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_EXPIRED_ATRECEIVER);
            expiredAtDispatchCounter = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_EXPIRED_ATDISPATCH);
            expiredAtInvokeCounter = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_EXPIRED_ATINVOKE);
            expiredAtRespondCounter = CounterStatistic.FindOrCreate(StatisticNames.MESSAGING_EXPIRED_ATRESPOND);

            perSocketDirectionStatsSend = new PerSocketDirectionStats[Enum.GetValues(typeof(SocketDirection)).Length];
            perSocketDirectionStatsReceive = new PerSocketDirectionStats[Enum.GetValues(typeof(SocketDirection)).Length];
            if (silo)
            {
                perSocketDirectionStatsSend[(int)SocketDirection.SiloToSilo] = new PerSocketDirectionStats(true, SocketDirection.SiloToSilo);
                perSocketDirectionStatsSend[(int)SocketDirection.GatewayToClient] = new PerSocketDirectionStats(true, SocketDirection.GatewayToClient);
                perSocketDirectionStatsReceive[(int)SocketDirection.SiloToSilo] = new PerSocketDirectionStats(false, SocketDirection.SiloToSilo);
                perSocketDirectionStatsReceive[(int)SocketDirection.GatewayToClient] = new PerSocketDirectionStats(false, SocketDirection.GatewayToClient);
            }
            else
            {
                perSocketDirectionStatsSend[(int)SocketDirection.ClientToGateway] = new PerSocketDirectionStats(true, SocketDirection.ClientToGateway);
                perSocketDirectionStatsReceive[(int)SocketDirection.ClientToGateway] = new PerSocketDirectionStats(false, SocketDirection.ClientToGateway);
            }

            perSiloSendCounters = new ConcurrentDictionary<string, CounterStatistic>();
            perSiloReceiveCounters = new ConcurrentDictionary<string, CounterStatistic>();
            perSiloPingSendCounters = new ConcurrentDictionary<string, CounterStatistic>();
            perSiloPingReceiveCounters = new ConcurrentDictionary<string, CounterStatistic>();
            perSiloPingReplyReceivedCounters = new ConcurrentDictionary<string, CounterStatistic>();
            perSiloPingReplyMissedCounters = new ConcurrentDictionary<string, CounterStatistic>();
        }

        internal static void OnMessageSend(SiloAddress targetSilo, Message.Directions direction, int numTotalBytes, int headerBytes, SocketDirection socketDirection)
        {
            if (numTotalBytes < 0)
                throw new ArgumentException(String.Format("OnMessageSend(numTotalBytes={0})", numTotalBytes), "numTotalBytes");
            OnMessageSend_Impl(targetSilo, direction, numTotalBytes, headerBytes, 1);
        }

        internal static void OnMessageBatchSend(SiloAddress targetSilo, Message.Directions direction, int numTotalBytes, int headerBytes, SocketDirection socketDirection, int numMsgsInBatch)
        {
            if (numTotalBytes < 0)
                throw new ArgumentException(String.Format("OnMessageBatchSend(numTotalBytes={0})", numTotalBytes), "numTotalBytes");
            OnMessageSend_Impl(targetSilo, direction, numTotalBytes, headerBytes, numMsgsInBatch);
            perSocketDirectionStatsSend[(int)socketDirection].OnMessage(numMsgsInBatch, numTotalBytes);
        }

        private static void OnMessageSend_Impl(SiloAddress targetSilo, Message.Directions direction, int numTotalBytes, int headerBytes, int numMsgsInBatch)
        {
            MessagesSentTotal.IncrementBy(numMsgsInBatch);
            MessagesSentPerDirection[(int)direction].IncrementBy(numMsgsInBatch);

            TotalBytesSent.IncrementBy(numTotalBytes);
            HeaderBytesSent.IncrementBy(headerBytes);
            sentMsgSizeHistogram.AddData(numTotalBytes);
            FindCounter(perSiloSendCounters, new StatisticName(StatisticNames.MESSAGING_SENT_MESSAGES_PER_SILO, (targetSilo != null ? targetSilo.ToString() : "Null")), CounterStorage.LogOnly).IncrementBy(numMsgsInBatch);
        }

        private static CounterStatistic FindCounter(ConcurrentDictionary<string, CounterStatistic> counters, StatisticName name, CounterStorage storage)
        {
            CounterStatistic stat;
            if (counters.TryGetValue(name.Name, out stat))
            {
                return stat;
            }
            stat = CounterStatistic.FindOrCreate(name, storage);
            counters.TryAdd(name.Name, stat);
            return stat;
        }

        internal static void OnMessageReceive(Message msg, int headerBytes, int bodyBytes)
        {
            MessagesReceived.Increment();
            MessagesReceivedPerDirection[(int)msg.Direction].Increment();
            totalBytesReceived.IncrementBy(headerBytes + bodyBytes);
            headerBytesReceived.IncrementBy(headerBytes);
            receiveMsgSizeHistogram.AddData(headerBytes + bodyBytes);
            SiloAddress addr = msg.SendingSilo;
            FindCounter(perSiloReceiveCounters, new StatisticName(StatisticNames.MESSAGING_RECEIVED_MESSAGES_PER_SILO, (addr != null ? addr.ToString() : "Null")), CounterStorage.LogOnly).Increment();
        }

        internal static void OnMessageBatchReceive(SocketDirection socketDirection, int numMsgsInBatch, int totalBytes)
        {
            perSocketDirectionStatsReceive[(int)socketDirection].OnMessage(numMsgsInBatch, totalBytes);
        }

        internal static void OnMessageExpired(Phase phase)
        {
            switch (phase)
            {
                case Phase.Send:
                    expiredAtSendCounter.Increment();
                    break;
                case Phase.Receive:
                    expiredAtReceiveCounter.Increment();
                    break;
                case Phase.Dispatch:
                    expiredAtDispatchCounter.Increment();
                    break;
                case Phase.Invoke:
                    expiredAtInvokeCounter.Increment();
                    break;
                case Phase.Respond:
                    expiredAtRespondCounter.Increment();
                    break;
            }
        }

        internal static void OnPingSend(SiloAddress destination)
        {
            FindCounter(perSiloPingSendCounters, new StatisticName(StatisticNames.MESSAGING_PINGS_SENT_PER_SILO, destination.ToString()), CounterStorage.LogOnly).Increment();
        }

        internal static void OnPingReceive(SiloAddress destination)
        {
            FindCounter(perSiloPingReceiveCounters, new StatisticName(StatisticNames.MESSAGING_PINGS_RECEIVED_PER_SILO, destination.ToString()), CounterStorage.LogOnly).Increment();
        }

        internal static void OnPingReplyReceived(SiloAddress replier)
        {
            FindCounter(perSiloPingReplyReceivedCounters, new StatisticName(StatisticNames.MESSAGING_PINGS_REPLYRECEIVED_PER_SILO, replier.ToString()), CounterStorage.LogOnly).Increment();
        }

        internal static void OnPingReplyMissed(SiloAddress replier)
        {
            FindCounter(perSiloPingReplyMissedCounters, new StatisticName(StatisticNames.MESSAGING_PINGS_REPLYMISSED_PER_SILO, replier.ToString()), CounterStorage.LogOnly).Increment();
        }

        internal static void OnFailedSentMessage(Message msg)
        {
            if (msg == null || !msg.HasDirection) return;
            int direction = (int)msg.Direction;
            if (FailedSentMessages[direction] == null)
            {
                FailedSentMessages[direction] = CounterStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.MESSAGING_SENT_FAILED_PER_DIRECTION, Enum.GetName(typeof(Message.Directions), direction)));
            }
            FailedSentMessages[direction].Increment();
        }

        internal static void OnDroppedSentMessage(Message msg)
        {
            if (msg == null || !msg.HasDirection) return;
            int direction = (int)msg.Direction;
            if (DroppedSentMessages[direction] == null)
            {
                DroppedSentMessages[direction] = CounterStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.MESSAGING_SENT_DROPPED_PER_DIRECTION, Enum.GetName(typeof(Message.Directions), direction)));
            }
            DroppedSentMessages[direction].Increment();
        }

        internal static void OnRejectedMessage(Message msg)
        {
            if (msg == null || !msg.HasDirection) return;
            int direction = (int)msg.Direction;
            if (RejectedMessages[direction] == null)
            {
                RejectedMessages[direction] = CounterStatistic.FindOrCreate(
                    new StatisticName(StatisticNames.MESSAGING_REJECTED_PER_DIRECTION, Enum.GetName(typeof(Message.Directions), direction)));
            }
            RejectedMessages[direction].Increment();
        }

        internal static void OnMessageReRoute(Message msg)
        {
            ReroutedMessages[(int)msg.Direction].Increment();
        }
    }
}
