using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Providers.Streams.Common;

namespace Orleans.Streams
{
    internal interface IPersistentStreamPullingAgent : ISystemTarget, IStreamProducerExtension
    {
        // The queue adapter have to be Immutable<>, since we want deliberately to pass it by reference.
        Task Initialize(Immutable<IQueueAdapter> queueAdapter, Immutable<IQueueAdapterCache> queueAdapterCache, Immutable<IStreamFailureHandler> deliveryFailureHandler);
        Task Shutdown();
    }

    internal interface IPersistentStreamPullingManager : ISystemTarget
    {
        Task Initialize(Immutable<IQueueAdapter> queueAdapter);
        Task Stop();
        Task StartAgents();
        Task StopAgents();
        Task<object> ExecuteCommand(PersistentStreamProviderCommand command, object arg);
    }
}
