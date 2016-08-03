
using System;
using System.Collections.Generic;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Generator
{
    [Serializable]
    internal class GeneratedBatchContainer : IBatchContainer
    {
        public Guid StreamGuid { get; private set; }
        public string StreamNamespace { get; private set; }
        public StreamSequenceToken SequenceToken { get { return RealToken; } }
        public EventSequenceToken RealToken { get; private set; }
        public object Payload { get; private set; }

        public GeneratedBatchContainer(Guid streamGuid, string streamNamespace, object payload, EventSequenceToken token)
        {
            StreamGuid = streamGuid;
            StreamNamespace = streamNamespace;
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

        public bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc)
        {
            return true;
        }
    }
}
