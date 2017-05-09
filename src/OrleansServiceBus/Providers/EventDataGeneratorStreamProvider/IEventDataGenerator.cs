#if NETSTANDARD
using Microsoft.Azure.EventHubs;
#else
using Microsoft.ServiceBus.Messaging;
#endif
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.ServiceBus.Providers
{
    internal interface IDataGenerator<T>
    {
        bool TryReadEvents(int maxCount, out IEnumerable<T> events);
    }

    internal interface IStreamDataGeneratingController
    {
        void AddDataGeneratorForStream(IStreamIdentity streamId);
        void StopProducingOnStream(IStreamIdentity streamId);
    }

    internal interface IStreamDataGenerator<T>: IDataGenerator<T>
    {
        IntCounter SequenceNumberCounter { set; }
        IStreamIdentity StreamId { get; }
        bool ShouldProduce { set; }
    }

    internal class IntCounter
    {
        private int counter = 0;
        public int Value { get { return this.counter; } }
        public void Increment()
        {
            counter++;
        }
    }
}
