using System;
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
        public StreamId StreamId;
        public IStreamConsumerExtension StreamConsumer;
        public StreamConsumerDataState State = StreamConsumerDataState.Inactive;
        public IQueueCacheCursor Cursor;
        public IStreamFilterPredicateWrapper Filter;
        public StreamHandshakeToken LastToken;

        public StreamConsumerData(GuidId subscriptionId, StreamId streamId, IStreamConsumerExtension streamConsumer, IStreamFilterPredicateWrapper filter)
        {
            SubscriptionId = subscriptionId;
            StreamId = streamId;
            StreamConsumer = streamConsumer;
            Filter = filter;
        }

        internal void SafeDisposeCursor(Logger logger)
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
