using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace Orleans.Docs.Snippets.Streaming;

public static class EventHubCachePressure
{
    public static void FavorSlowConsumers(
        ISiloEventHubStreamConfigurator configurator)
    {
        // <event_hub_slow_consumer_pressure>
        configurator.ConfigureCachePressuring(builder => builder.Configure(options =>
        {
            options.AveragingCachePressureMonitorFlowControlThreshold = null;
            options.SlowConsumingMonitorFlowControlThreshold = 0.7;
            options.SlowConsumingMonitorPressureWindowSize =
                TimeSpan.FromSeconds(10);
        }));
        // </event_hub_slow_consumer_pressure>
    }
}
