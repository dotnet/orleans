using System;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Streams
{
    [Serializable]
    internal enum StreamConsumerDataState
    {
        Active, // Indicates that events are activly being delivered to this consumer.
        Inactive, // Indicates that events are not activly being delivered to this consumers.  If adapter produces any events on this consumers stream, the agent will need begin delivering events
    }

    [Serializable]
    internal class StreamConsumerData
    {
        public GuidId SubscriptionId;
        public InternalStreamId StreamId;
        public IStreamConsumerExtension StreamConsumer;
        public StreamConsumerDataState State = StreamConsumerDataState.Inactive;
        public IQueueCacheCursor Cursor;
        public StreamHandshakeToken LastToken;
        public string FilterData;

        public StreamConsumerData(GuidId subscriptionId, InternalStreamId streamId, IStreamConsumerExtension streamConsumer, string filterData)
        {
            SubscriptionId = subscriptionId;
            StreamId = streamId;
            StreamConsumer = streamConsumer;
            FilterData = filterData;
        }

        internal void SafeDisposeCursor(ILogger logger)
        {
            try
            {
                if (Cursor != null)
                {
                    // kill cursor activity and ensure it does not start again on this consumer data.
                    Utils.SafeExecute(Cursor.Dispose, logger,
                        () => String.Format("Cursor.Dispose on stream {0}, StreamConsumer {1} has thrown exception.", StreamId, StreamConsumer));
                }
            }
            finally
            {
                Cursor = null;
            }
        }
    }
}
