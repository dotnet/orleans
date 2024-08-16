using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime.Placement.Rebalancing;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Hosting;

#nullable enable

public static class ActivationRebalancerExtensions
{
    [Experimental("ORLEANSEXP002")]
    public static ISiloBuilder AddActivationRebalancer(this ISiloBuilder builder)
    {
        builder.AddStartupTask<ActivationRebalancerTrigger>();
        builder.Services.AddTransient<IConfigurationValidator, ActivationRebalancerOptionsValidator>();

        return builder;
    }
}