using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Placement.Rebalancing;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.Runtime.Placement.Rebalancing;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Hosting;

#nullable enable
public static class ActiveRebalancingExtensions
{
    /// <summary>
    /// Adds support for active-rebalancing in this silo.
    /// </summary>
    [Experimental("ORLEANSEXP001")]
    public static ISiloBuilder AddActiveRebalancing(this ISiloBuilder builder)
        => builder.AddActiveRebalancing<DefaultImbalanceRule>();

    /// <summary>
    /// Adds support for active-rebalancing in this silo.
    /// </summary>
    [Experimental("ORLEANSEXP001")]
    public static ISiloBuilder AddActiveRebalancing<TRule>(this ISiloBuilder builder) where TRule : class, IImbalanceToleranceRule
        => builder
            .ConfigureServices(services => services.AddActiveRebalancing<TRule>());

    private static IServiceCollection AddActiveRebalancing<TRule>(this IServiceCollection services) where TRule : class, IImbalanceToleranceRule
    {
        services.AddTransient<IConfigurationValidator, ActiveRebalancingOptionsValidator>();
        
        if (typeof(TRule) == typeof(DefaultImbalanceRule))
        {
            services.AddSingleton<DefaultImbalanceRule>();
            services.AddFromExisting<IImbalanceToleranceRule, DefaultImbalanceRule>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, DefaultImbalanceRule>();
        }
        else
        {
            services.AddSingleton<IImbalanceToleranceRule, TRule>();
        }

        services.AddSingleton<IRebalancingMessageFilter, RebalancingMessageFilter>();
        services.AddSingleton<ActivationRebalancer>();
        services.AddFromExisting<IMessageStatisticsSink, ActivationRebalancer>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, ActivationRebalancer>();

        return services;
    }
}