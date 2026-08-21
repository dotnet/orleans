using System;
using Microsoft.Extensions.Options;
using Orleans.Hosting;

namespace Orleans.Hosting.Kubernetes;

internal sealed class KubernetesClusterMonitoringOptions : IOptionsMonitor<ClusterMonitoringOptions>
{
    private readonly IOptionsMonitor<KubernetesHostingOptions> _options;

    public KubernetesClusterMonitoringOptions(IOptionsMonitor<KubernetesHostingOptions> options)
    {
        _options = options;
    }

    public ClusterMonitoringOptions CurrentValue => Create(_options.CurrentValue);

    public ClusterMonitoringOptions Get(string? name) => Create(_options.Get(name));

    public IDisposable? OnChange(Action<ClusterMonitoringOptions, string?> listener) =>
        _options.OnChange((options, name) => listener(Create(options), name));

    private static ClusterMonitoringOptions Create(KubernetesHostingOptions options) =>
        new()
        {
            MaxAgents = options.MaxAgents,
            MaxInitializationAttempts = options.MaxKubernetesApiRetryAttempts,
            DeleteDefunctClusterMembers = options.DeleteDefunctSiloPods
        };
}
