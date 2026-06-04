using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;
using Orleans.Messaging;

#nullable disable
namespace Orleans.Runtime
{
    internal sealed class MessagingInstruments
    {
        private long _headerBytesSent;
        private long _headerBytesReceived;

        public MessagingInstruments(OrleansInstruments instruments)
        {
            HeaderBytesSentCounter = instruments.Meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_SENT_BYTES_HEADER, () => _headerBytesSent, "bytes");
            HeaderBytesReceivedCounter = instruments.Meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_RECEIVED_BYTES_HEADER, () => _headerBytesReceived, "bytes");
            LocalMessagesSentCounter = instruments.Meter.CreateObservableCounter<long>(InstrumentNames.MESSAGING_SENT_LOCALMESSAGES, LocalMessagesSentCounterAggregator.Collect);
            FailedSentMessagesCounter = instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_SENT_FAILED);
            DroppedSentMessagesCounter = instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_SENT_DROPPED);
            RejectedMessagesCounter = instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_REJECTED);
            ReroutedMessagesCounter = instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_REROUTED);
            ExpiredMessagesCounter = instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_EXPIRED);
            ConnectedClient = instruments.Meter.CreateUpDownCounter<int>(InstrumentNames.GATEWAY_CONNECTED_CLIENTS);
            PingSendCounter = instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_PINGS_SENT);
            PingReceivedCounter = instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_PINGS_RECEIVED);
            PingReplyReceivedCounter = instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_PINGS_REPLYRECEIVED);
            PingReplyMissedCounter = instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_PINGS_REPLYMISSED);
            MessageSentSizeHistogram = instruments.Meter.CreateHistogram<int>(InstrumentNames.MESSAGING_SENT_MESSAGES_SIZE, "bytes");
            MessageReceivedSizeHistogram = instruments.Meter.CreateHistogram<int>(InstrumentNames.MESSAGING_RECEIVED_MESSAGES_SIZE, "bytes");
        }

        internal readonly ObservableCounter<long> HeaderBytesSentCounter;
        internal readonly ObservableCounter<long> HeaderBytesReceivedCounter;
        internal readonly CounterAggregator LocalMessagesSentCounterAggregator = new();
        private readonly ObservableCounter<long> LocalMessagesSentCounter;

        internal readonly Counter<int> FailedSentMessagesCounter;
        internal readonly Counter<int> DroppedSentMessagesCounter;
        internal readonly Counter<int> RejectedMessagesCounter;
        internal readonly Counter<int> ReroutedMessagesCounter;
        internal readonly Counter<int> ExpiredMessagesCounter;

        internal readonly UpDownCounter<int> ConnectedClient;
        internal readonly Counter<int> PingSendCounter;
        internal readonly Counter<int> PingReceivedCounter;
        internal readonly Counter<int> PingReplyReceivedCounter;
        internal readonly Counter<int> PingReplyMissedCounter;

        // currently, bucket size need to be configured at collector side
        // [Add "hints" in Metric API to provide things like histogram bounds]
        // https://github.com/dotnet/runtime/issues/63650
        // Histogram of sent  message size, starting from 0 in multiples of 2
        // (1=2^0, 2=2^2, ... , 256=2^8, 512=2^9, 1024==2^10, ... , up to ... 2^30=1GB)
        internal readonly Histogram<int> MessageSentSizeHistogram;
        internal readonly Histogram<int> MessageReceivedSizeHistogram;

        internal enum Phase
        {
            Send,
            Receive,
            Dispatch,
            Invoke,
            Respond,
        }

        internal void OnMessageExpired(Phase phase)
        {
            ExpiredMessagesCounter.Add(1, new KeyValuePair<string, object>("Phase", phase));
        }

        internal void OnPingSend(SiloAddress destination)
        {
            PingSendCounter.Add(1, new KeyValuePair<string, object>("Destination", destination.ToString()));
        }

        internal void OnPingReceive(SiloAddress destination)
        {
            PingReceivedCounter.Add(1, new KeyValuePair<string, object>("Destination", destination.ToString()));
        }

        internal void OnPingReplyReceived(SiloAddress replier)
        {
            PingReplyReceivedCounter.Add(1, new KeyValuePair<string, object>("Destination", replier.ToString()));
        }

        internal void OnPingReplyMissed(SiloAddress replier)
        {
            PingReplyMissedCounter.Add(1, new KeyValuePair<string, object>("Destination", replier.ToString()));
        }

        internal void OnFailedSentMessage(Message msg)
        {
            if (msg == null || !msg.HasDirection) return;
            FailedSentMessagesCounter.Add(1, new KeyValuePair<string, object>("Direction", msg.Direction.ToString()));
        }

        internal void OnDroppedSentMessage(Message msg)
        {
            if (msg == null || !msg.HasDirection) return;
            DroppedSentMessagesCounter.Add(1, new KeyValuePair<string, object>("Direction", msg.Direction.ToString()));
        }

        internal void OnRejectedMessage(Message msg)
        {
            if (msg == null || !msg.HasDirection) return;
            RejectedMessagesCounter.Add(1, new KeyValuePair<string, object>("Direction", msg.Direction.ToString()));
        }

        internal void OnMessageReRoute(Message msg)
        {
            ReroutedMessagesCounter.Add(1, new KeyValuePair<string, object>("Direction", msg.Direction.ToString()));
        }

        internal void OnMessageReceive(Message msg, int numTotalBytes, int headerBytes, ConnectionDirection connectionDirection, SiloAddress remoteSiloAddress = null)
        {
            if (MessageReceivedSizeHistogram.Enabled)
            {
                if (remoteSiloAddress != null)
                {
                    MessageReceivedSizeHistogram.Record(numTotalBytes, new KeyValuePair<string, object>("ConnectionDirection", connectionDirection.ToString()), new KeyValuePair<string, object>("MessageDirection", msg.Direction.ToString()), new KeyValuePair<string, object>("silo", remoteSiloAddress));
                }
                else
                {
                    MessageReceivedSizeHistogram.Record(numTotalBytes, new KeyValuePair<string, object>("ConnectionDirection", connectionDirection.ToString()), new KeyValuePair<string, object>("MessageDirection", msg.Direction.ToString()));
                }
            }

            Interlocked.Add(ref _headerBytesReceived, headerBytes);
        }

        internal void OnMessageSend(Message msg, int numTotalBytes, int headerBytes, ConnectionDirection connectionDirection, SiloAddress remoteSiloAddress = null)
        {
            Debug.Assert(numTotalBytes >= 0, $"OnMessageSend(numTotalBytes={numTotalBytes})");

            if (MessageSentSizeHistogram.Enabled)
            {
                if (remoteSiloAddress != null)
                {
                    MessageSentSizeHistogram.Record(numTotalBytes, new KeyValuePair<string, object>("ConnectionDirection", connectionDirection.ToString()), new KeyValuePair<string, object>("MessageDirection", msg.Direction.ToString()), new KeyValuePair<string, object>("silo", remoteSiloAddress));
                }
                else
                {
                    MessageSentSizeHistogram.Record(numTotalBytes, new KeyValuePair<string, object>("ConnectionDirection", connectionDirection.ToString()), new KeyValuePair<string, object>("MessageDirection", msg.Direction.ToString()));
                }
            }

            Interlocked.Add(ref _headerBytesSent, headerBytes);
        }
    }
}
