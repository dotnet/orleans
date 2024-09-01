using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Placement;
using Orleans.Runtime;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using System.Threading;

namespace Orleans.Hosting;

#nullable enable

public static class ActivationRebalancerExtensions
{
    [Experimental("ORLEANSEXP002")]
    public static ISiloBuilder AddActivationRebalancer(this ISiloBuilder builder)
    {
        builder.AddStartupTask<StartupTask>();
        builder.Services.AddTransient<IConfigurationValidator, ActivationRebalancerOptionsValidator>();

        return builder;
    }

    private sealed class StartupTask(IGrainFactory grainFactory) : IStartupTask
    {
        public Task Execute(CancellationToken cancellationToken) =>
            grainFactory.GetGrain<IInternalActivationRebalancerGrain>(
                IActivationRebalancerGrain.Key).StartRebalancing();
    }
}