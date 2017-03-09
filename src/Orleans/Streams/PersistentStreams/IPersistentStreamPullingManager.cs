using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Providers.Streams.Common;

namespace Orleans.Streams
{
    internal interface IPersistentStreamPullingManager : ISystemTarget
    {
        Task Initialize(Immutable<IQueueAdapter> queueAdapter);
        Task Stop();
        Task StartAgents();
        Task StopAgents();
        Task<object> ExecuteCommand(PersistentStreamProviderCommand command, object arg);
    }
}