using Orleans.Runtime;
using System.Collections.Generic;

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
        void AddDataGeneratorForStream(StreamId streamId);
        /// <summary>
        /// Ask one stream to stop producing
        /// </summary>
        /// <param name="streamId"></param>
        void StopProducingOnStream(StreamId streamId);
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
        StreamId StreamId { get; }
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
