using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Streams
{
    public static class QueueAdapterExtensions
    {
        /// <summary>Writes a set of events to the queue as a single batch associated with the provided streamId.</summary>
        public static Task QueueMessageAsync<T>(this IQueueAdapter adapter, Guid streamGuid, String streamNamespace, T evt, StreamSequenceToken token, Dictionary<string, object> requestContext)
        {
            return adapter.QueueMessageBatchAsync(streamGuid, streamNamespace, new[] { evt }, token, requestContext);
        }
    }
}