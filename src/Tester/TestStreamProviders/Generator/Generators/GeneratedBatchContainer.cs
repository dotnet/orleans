using System;
using System.Collections.Generic;
using Orleans.Streams;

namespace Tester.TestStreamProviders.Generator.Generators
{
    [Serializable]
    internal class GeneratedBatchContainer : IBatchContainer
    {
        private readonly object payload;

        public Guid StreamGuid { get; private set; }
        public string StreamNamespace { get; private set; }
        public StreamSequenceToken SequenceToken { get; set; }

        public GeneratedBatchContainer(Guid streamGuid, string streamNamespace, object payload, StreamSequenceToken token)
        {
            StreamGuid = streamGuid;
            StreamNamespace = streamNamespace;
            this.payload = payload;
            this.SequenceToken = token;
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return new[] { Tuple.Create((T)payload, SequenceToken) };
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
