
using System;
using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Generator
{
    [GenerateSerializer]
    internal class GeneratedBatchContainer : IBatchContainer
    {
        [Id(0)]
        public StreamId StreamId { get; }

        public StreamSequenceToken SequenceToken => RealToken;

        [Id(1)]
        public EventSequenceTokenV2 RealToken { get;  }

        [Id(2)]
        public DateTime EnqueueTimeUtc { get; }

        [Id(3)]
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
