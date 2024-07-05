using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Placement.Rebalancing;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.Runtime.Placement.Rebalancing;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Orleans.Hosting;

#nullable enable
public static class ActiveRebalancingExtensions
{
    private static readonly ServiceDescriptor ActivationRebalancerServiceDescriptor = new(typeof(ActivationRebalancer), typeof(ActivationRebalancer));

    /// <summary>
    /// Adds support for active-rebalancing in this silo.
    /// </summary>
    [Experimental("ORLEANSEXP001")]
    public static ISiloBuilder AddActiveRebalancing(this ISiloBuilder builder)
        => builder.AddActiveRebalancing<DefaultImbalanceRule>();

    /// <summary>
    /// Adds support for active-rebalancing in this silo.
    /// </summary>
    /// <typeparam name="TRule">The type of the imbalance rule to use.</typeparam>
    [Experimental("ORLEANSEXP001")]
    public static ISiloBuilder AddActiveRebalancing<TRule>(this ISiloBuilder builder) where TRule : class, IImbalanceToleranceRule
        => builder
            .ConfigureServices(services => services.AddActiveRebalancing<TRule>());

    private static IServiceCollection AddActiveRebalancing<TRule>(this IServiceCollection services) where TRule : class, IImbalanceToleranceRule
    {
        if (!services.Contains(ActivationRebalancerServiceDescriptor))
        {
            services.Add(ActivationRebalancerServiceDescriptor);
            services.AddSingleton<IRebalancingMessageFilter, RebalancingMessageFilter>();
            services.AddFromExisting<IMessageStatisticsSink, ActivationRebalancer>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, ActivationRebalancer>();
            services.AddTransient<IConfigurationValidator, ActiveRebalancingOptionsValidator>();
        }

        services.AddSingleton<TRule>();
        services.AddFromExisting<IImbalanceToleranceRule, TRule>();
        if (typeof(TRule).IsAssignableTo(typeof(ILifecycleParticipant<ISiloLifecycle>)))
        {
            services.AddFromExisting(typeof(ILifecycleParticipant<ISiloLifecycle>), typeof(TRule));
        }

        return services;
    }
}