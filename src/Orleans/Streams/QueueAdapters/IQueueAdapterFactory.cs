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

        Task<IQueueAdapter> CreateAdapter();

        IQueueAdapterCache GetQueueAdapterCache();

        IStreamQueueMapper GetStreamQueueMapper();

        Task<IStreamFailureHandler> GetDeliveryFailureHandler(QueueId queueId);
    }
}
