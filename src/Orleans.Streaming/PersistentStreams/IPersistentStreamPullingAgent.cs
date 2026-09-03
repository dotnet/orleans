using System.Threading;
using System.Threading.Tasks;
using Orleans.Providers.Streams.Common;

namespace Orleans.Streams
{
    internal interface IPersistentStreamPullingAgent : ISystemTarget, IStreamProducerExtension
    {
        [Alias("06009D9C")]
        Task Initialize(CancellationToken cancellationToken = default);
        [Alias("620FF905")]
        Task Shutdown(CancellationToken cancellationToken = default);
    }

    internal interface IPersistentStreamPullingManager : ISystemTarget
    {
        [Alias("455AB850")]
        Task Initialize(CancellationToken cancellationToken = default);
        [Alias("F4B5B5AA")]
        Task Stop(CancellationToken cancellationToken = default);
        [Alias("54E9E970")]
        Task StartAgents(CancellationToken cancellationToken = default);
        [Alias("BBD50CFF")]
        Task StopAgents(CancellationToken cancellationToken = default);
        [Alias("DE756D95")]
        Task<object?> ExecuteCommand(PersistentStreamProviderCommand command, object? arg, CancellationToken cancellationToken = default);
    }
}
