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

namespace Orleans.ServiceBus.Providers.Testing
{
    /// <summary>
    /// Data generator for test purpose
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IDataGenerator<T>
    {
        /// <summary>
        /// Data generator mimic event reading
        /// </summary>
        /// <param name="maxCount"></param>
        /// <param name="events"></param>
        /// <returns></returns>
        bool TryReadEvents(int maxCount, out IEnumerable<T> events);
    }

    /// <summary>
    /// StreamDataGeneratingController control stream data generating start and stop
    /// </summary>
    public interface IStreamDataGeneratingController
    {
        /// <summary>
        /// configure data generator for a stream
        /// </summary>
        /// <param name="streamId"></param>
        void AddDataGeneratorForStream(IStreamIdentity streamId);
        /// <summary>
        /// Ask one stream to stop producing
        /// </summary>
        /// <param name="streamId"></param>
        void StopProducingOnStream(IStreamIdentity streamId);
    }

    /// <summary>
    /// data generator for a specific stream
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IStreamDataGenerator<T>: IDataGenerator<T>
    {
        /// <summary>
        /// counter for sequence number
        /// </summary>
        IIntCounter SequenceNumberCounter { set; }
        /// <summary>
        /// Stream identity for this data generator
        /// </summary>
        IStreamIdentity StreamId { get; }
        /// <summary>
        /// 
        /// </summary>
        bool ShouldProduce { set; }
    }

    /// <summary>
    /// counter for integer
    /// </summary>
    public interface IIntCounter
    {
        /// <summary>
        /// counter value
        /// </summary>
        int Value { get; }
        /// <summary>
        /// increment the counter
        /// </summary>
        void Increment();
    }

    internal class IntCounter : IIntCounter
    {
        private int counter = 0;
        public int Value { get { return this.counter; } }
        public void Increment()
        {
            counter++;
        }
    }
}
