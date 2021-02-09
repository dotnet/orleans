
using System;
using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Generator
{
    internal class GeneratedBatchContainer : IBatchContainer
    {
        public StreamId StreamId { get; }
        public StreamSequenceToken SequenceToken => RealToken;
        public EventSequenceTokenV2 RealToken { get;  }
        public DateTime EnqueueTimeUtc { get; }
        public object Payload { get; }

        public GeneratedBatchContainer(StreamId streamId, object payload, EventSequenceTokenV2 token)
        {
            StreamId = streamId;
            EnqueueTimeUtc = DateTime.UtcNow;
            this.Payload = payload;
            this.RealToken = token;
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return new[] { Tuple.Create((T)Payload, SequenceToken) };
        }

        public bool ImportRequestContext()
        {
            return false;
        }
    }
}
