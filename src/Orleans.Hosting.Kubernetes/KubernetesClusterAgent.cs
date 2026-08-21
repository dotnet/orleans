using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting.Clustering;
using Orleans.Runtime;

namespace Orleans.Hosting.Kubernetes;

/// <summary>
/// Reflects cluster configuration changes between Orleans and Kubernetes.
/// </summary>
public sealed partial class KubernetesClusterAgent : ILifecycleParticipant<ISiloLifecycle>
{
    private readonly ClusterAgent _agent;

    public KubernetesClusterAgent(
        IClusterMembershipService clusterMembershipService,
        ILogger<KubernetesClusterAgent> logger,
        IOptionsMonitor<KubernetesHostingOptions> options,
        IOptions<ClusterOptions> clusterOptions,
        ILocalSiloDetails localSiloDetails)
    {
        var provider = new KubernetesClusterProvider(
            new LoggerAdapter<KubernetesClusterProvider>(logger),
            options,
            clusterOptions);
        _agent = new ClusterAgent(
            clusterMembershipService,
            new LoggerAdapter<ClusterAgent>(logger),
            new KubernetesClusterMonitoringOptions(options),
            provider,
            localSiloDetails);
    }

    public void Participate(ISiloLifecycle lifecycle) => _agent.Participate(lifecycle);

    public Task OnStop(CancellationToken cancellationToken) => _agent.OnStop(cancellationToken);

    private sealed class LoggerAdapter<T> : ILogger<T>
    {
        private readonly ILogger _logger;

        public LoggerAdapter(ILogger logger)
        {
            _logger = logger;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _logger.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _logger.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _logger.Log(logLevel, eventId, state, exception, formatter);
    }
}
