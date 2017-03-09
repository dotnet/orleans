using System.Threading.Tasks;

namespace Orleans.Streams
{
    /// <summary>
    /// Provider-facing interface for manager of streaming providers
    /// </summary>
    internal interface ISiloSideStreamProviderRuntime : IStreamProviderRuntime
    {
        /// <summary>Start the pulling agents for a given persistent stream provider.</summary>
        Task<IPersistentStreamPullingManager> InitializePullingAgents(
            string streamProviderName,
            IQueueAdapterFactory adapterFactory,
            IQueueAdapter queueAdapter,
            PersistentStreamProviderConfig config);
    }
}