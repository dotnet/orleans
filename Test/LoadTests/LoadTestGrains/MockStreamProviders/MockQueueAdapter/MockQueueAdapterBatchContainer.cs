using System;
using System.Collections.Generic;
using System.Linq;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

namespace OrleansProviders.PersistentStream.MockQueueAdapter
{
    [Serializable]
    public abstract class MockQueueAdapterBatchContainer : IBatchContainer
    {
        public Guid StreamGuid { get; private set; }

        public String StreamNamespace { get; private set; }

        public StreamSequenceToken SequenceToken
        {
            get { return EventSequenceToken; }
        }

        internal EventSequenceToken EventSequenceToken;

        protected MockQueueAdapterBatchContainer(Guid streamGuid, String streamNamespace)
        {
            StreamGuid = streamGuid;
            StreamNamespace = streamNamespace;
        }

        public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
        {
            return GetEventsInternal<T>().Select((e, i) =>
                new Tuple<T, StreamSequenceToken>(e, EventSequenceToken.CreateSequenceTokenForEvent(i)));
        }

        protected abstract IEnumerable<T> GetEventsInternal<T>();

        public abstract bool ShouldDeliver(IStreamIdentity stream, object filterData, StreamFilterPredicate shouldReceiveFunc);

        public override string ToString()
        {
            return string.Format("MockQueueAdapterBatchContainer: Stream={0}, Namespace={1}", StreamGuid, StreamNamespace);
        }
    }
}