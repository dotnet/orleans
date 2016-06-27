using System;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Adapter factory.  This should create an adapter from the stream provider configuration
    /// </summary>
    public interface IQueueAdapterFactory
    {
        void Init(IProviderConfiguration config, string providerName, Logger logger, IServiceProvider serviceProvider);

        /// <summary>
        /// Create queue adapter.
        /// </summary>
        /// <returns></returns>
        Task<IQueueAdapter> CreateAdapter();

        /// <summary>
        /// Create queue message cache adapter
        /// </summary>
        /// <returns></returns>
        IQueueAdapterCache GetQueueAdapterCache();

        /// <summary>
        /// Create queue mapper
        /// </summary>
        /// <returns></returns>
        IStreamQueueMapper GetStreamQueueMapper();

        /// <summary>
        /// Aquire delivery failure handler for a queue
        /// </summary>
        /// <param name="queueId"></param>
        /// <returns></returns>
        Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId);
    }
}
