using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Orleans.Messaging;

namespace Orleans.Runtime
{
    internal static class MessagingInstruments
    {
        internal static readonly Counter<int> HeaderBytesSentCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_SENT_BYTES_HEADER, "bytes");
        internal static readonly Counter<int> HeaderBytesReceivedCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_RECEIVED_BYTES_HEADER, "bytes");
        internal static readonly Counter<int> LocalMessagesSentCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_SENT_LOCALMESSAGES);
        internal static readonly Counter<int> FailedSentMessagesCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_SENT_FAILED);
        internal static readonly Counter<int> DroppedSentMessagesCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_SENT_DROPPED);
        internal static readonly Counter<int> RejectedMessagesCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_REJECTED);
        internal static readonly Counter<int> ReroutedMessagesCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_REROUTED);
        internal static readonly Counter<int> ExpiredMessagesCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_EXPIRED);

        internal static readonly Counter<int> ConnectedClient = Instruments.Meter.CreateCounter<int>(InstrumentNames.GATEWAY_CONNECTED_CLIENTS);
        internal static readonly Counter<int> PingSendCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_PINGS_SENT);
        internal static readonly Counter<int> PingReceivedCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_PINGS_RECEIVED);
        internal static readonly Counter<int> PingReplyReceivedCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_PINGS_REPLYRECEIVED);
        internal static readonly Counter<int> PingReplyMissedCounter = Instruments.Meter.CreateCounter<int>(InstrumentNames.MESSAGING_PINGS_REPLYMISSED);

        // currently, bucket size need to be configured at collector side
        // [Add "hints" in Metric API to provide things like histogram bounds]
        // https://github.com/dotnet/runtime/issues/63650
        // Histogram of sent  message size, starting from 0 in multiples of 2
        // (1=2^0, 2=2^2, ... , 256=2^8, 512=2^9, 1024==2^10, ... , up to ... 2^30=1GB)
        internal static readonly Histogram<int> MessageSentSizeHistogram = Instruments.Meter.CreateHistogram<int>(InstrumentNames.MESSAGING_SENT_MESSAGES_SIZE, "bytes");
        internal static readonly Histogram<int> MessageReceivedSizeHistogram = Instruments.Meter.CreateHistogram<int>(InstrumentNames.MESSAGING_RECEIVED_MESSAGES_SIZE, "bytes");


        internal enum Phase
        {
            Send,
            Receive,
            Dispatch,
            Invoke,
            Respond,
        }

        internal static void OnMessageExpired(Phase phase)
        {
            ExpiredMessagesCounter.Add(1, new KeyValuePair<string, object>("Phase", phase));
        }

        internal static void OnPingSend(SiloAddress destination)
        {
            PingSendCounter.Add(1, new KeyValuePair<string, object>("Destination", destination.ToString()));
        }

        internal static void OnPingReceive(SiloAddress destination)
        {
            PingReceivedCounter.Add(1, new KeyValuePair<string, object>("Destination", destination.ToString()));
        }

        internal static void OnPingReplyReceived(SiloAddress replier)
        {
            PingReplyReceivedCounter.Add(1, new KeyValuePair<string, object>("Destination", replier.ToString()));
        }

        internal static void OnPingReplyMissed(SiloAddress replier)
        {
            PingReplyMissedCounter.Add(1, new KeyValuePair<string, object>("Destination", replier.ToString()));
        }

        internal static void OnFailedSentMessage(Message msg)
        {
            if (msg == null || !msg.HasDirection) return;
            FailedSentMessagesCounter.Add(1, new KeyValuePair<string, object>("Direction", msg.Direction.ToString()));
        }

        internal static void OnDroppedSentMessage(Message msg)
        {
            if (msg == null || !msg.HasDirection) return;
            DroppedSentMessagesCounter.Add(1, new KeyValuePair<string, object>("Direction", msg.Direction.ToString()));
        }

        internal static void OnRejectedMessage(Message msg)
        {
            if (msg == null || !msg.HasDirection) return;
            RejectedMessagesCounter.Add(1, new KeyValuePair<string, object>("Direction", msg.Direction.ToString()));
        }

        internal static void OnMessageReRoute(Message msg)
        {
            ReroutedMessagesCounter.Add(1, new KeyValuePair<string, object>("Direction", msg.Direction.ToString()));
        }

        internal static void OnMessageReceive(Message msg, int numTotalBytes, int headerBytes, ConnectionDirection connectionDirection, SiloAddress remoteSiloAddress = null)
        {
            if (remoteSiloAddress != null)
            {
                MessageReceivedSizeHistogram.Record(numTotalBytes, new KeyValuePair<string, object>("ConnectionDirection", connectionDirection.ToString()), new KeyValuePair<string, object>("MessageDirection", msg.Direction.ToString()), new KeyValuePair<string, object>("silo", remoteSiloAddress));
            }
            else
            {
                MessageReceivedSizeHistogram.Record(numTotalBytes, new KeyValuePair<string, object>("ConnectionDirection", connectionDirection.ToString()), new KeyValuePair<string, object>("MessageDirection", msg.Direction.ToString()));
            }
            HeaderBytesReceivedCounter.Add(headerBytes);
        }
        internal static void OnMessageSend(Message msg, int numTotalBytes, int headerBytes, ConnectionDirection connectionDirection, SiloAddress remoteSiloAddress = null)
        {
            Debug.Assert(numTotalBytes >= 0, $"OnMessageSend(numTotalBytes={numTotalBytes})");

            if (remoteSiloAddress != null)
            {
                MessageSentSizeHistogram.Record(numTotalBytes, new KeyValuePair<string, object>("ConnectionDirection", connectionDirection.ToString()), new KeyValuePair<string, object>("MessageDirection", msg.Direction.ToString()), new KeyValuePair<string, object>("silo", remoteSiloAddress));
            }
            else
            {
                MessageSentSizeHistogram.Record(numTotalBytes, new KeyValuePair<string, object>("ConnectionDirection", connectionDirection.ToString()), new KeyValuePair<string, object>("MessageDirection", msg.Direction.ToString()));
            }
            HeaderBytesSentCounter.Add(headerBytes);
        }
    }
}
