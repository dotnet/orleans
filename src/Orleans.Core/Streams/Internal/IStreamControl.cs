using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Stream control interface to allow stream runtime to perform management operations on streams 
    /// without needing to worry about concrete generic types used by this stream
    /// </summary>
    internal interface IStreamControl
    {
        /// <summary>
        /// Perform cleanup functions for this stream.
        /// </summary>
        /// <returns>Completion promise for the cleanup operstions for this stream.</returns>
        Task Cleanup(bool cleanupProducers, bool cleanupConsumers);
    }
}
