using System;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

#nullable enable
namespace Orleans.Streams
{
    [Serializable]
    internal enum StreamConsumerDataState
    {
        Active, // Indicates that events are activly being delivered to this consumer.
        Inactive, // Indicates that events are not activly being delivered to this consumers.  If adapter produces any events on this consumers stream, the agent will need begin delivering events
    }

    [Serializable]
    [GenerateSerializer]
    internal sealed class StreamConsumerData
    {
        [Id(0)]
        public GuidId SubscriptionId;
        [Id(1)]
        public QualifiedStreamId StreamId;
        [Id(2)]
        public IStreamConsumerExtension StreamConsumer;
        [Id(3)]
        public StreamConsumerDataState State = StreamConsumerDataState.Inactive;
        [Id(4)]
        public IQueueCacheCursor? Cursor;
        [Id(5)]
        public StreamHandshakeToken? LastToken;
        [Id(6)]
        public string FilterData;

        public StreamConsumerData(GuidId subscriptionId, QualifiedStreamId streamId, IStreamConsumerExtension streamConsumer, string filterData)
        {
            SubscriptionId = subscriptionId;
            StreamId = streamId;
            StreamConsumer = streamConsumer;
            FilterData = filterData;
        }

        internal void SafeDisposeCursor(ILogger logger)
        {
            if (Cursor is { } cursor)
            {
                Cursor = null;
                // kill cursor activity and ensure it does not start again on this consumer data.
                try
                {
                    cursor.Dispose();
                }
                catch (Exception ex)
                {
                    string? caller = null;
                    try
                    {
                        caller = $"Cursor.Dispose on stream {StreamId}, StreamConsumer {StreamConsumer} has thrown exception.";
                    }
                    catch { }
                    Utils.LogIgnoredException(logger, ex, caller);
                }
            }
        }
    }
}
