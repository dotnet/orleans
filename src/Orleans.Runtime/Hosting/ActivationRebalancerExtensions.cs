using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime;
using System.Diagnostics.CodeAnalysis;
using Orleans.Configuration.Internal;
using Orleans.Placement.Rebalancing;
using Orleans.Runtime.Placement.Rebalancing;

namespace Orleans.Hosting;

#nullable enable

public static class ActivationRebalancerExtensions
{
    /// <summary>
    /// Enables activation rebalancing for the cluster.
    /// </summary>
    /// <remarks>
    /// Activation rebalancing attempts to distribute activations around the cluster in such a
    /// way that it optimizes both activation count and memory usages across the cluster.
    /// <para>You can read more on activation rebalancing <see href="https://www.ledjonbehluli.com/posts/orleans_adaptive_rebalancing/">here</see></para>
    /// </remarks>
    [Experimental("ORLEANSEXP002")]
    public static ISiloBuilder AddActivationRebalancer(this ISiloBuilder builder) =>
        builder.ConfigureServices(service => service.AddActivationRebalancer<FailedSessionBackoffProvider>());

    /// <inheritdoc cref="AddActivationRebalancer(ISiloBuilder)"/>.
    /// <typeparam name="TProvider">Custom backoff provider for determining next session after a failed attempt.</typeparam>
    [Experimental("ORLEANSEXP002")]
    public static IServiceCollection AddActivationRebalancer<TProvider>(this IServiceCollection services)
        where TProvider : class, IFailedRebalancingSessionBackoffProvider
    {
        services.AddSingleton<ActivationRebalancerMonitor>();
        services.AddFromExisting<IActivationRebalancer, ActivationRebalancerMonitor>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, ActivationRebalancerMonitor>();
        services.AddTransient<IConfigurationValidator, ActivationRebalancerOptionsValidator>();
        
        services.AddSingleton<TProvider>();
        services.AddFromExisting<IFailedRebalancingSessionBackoffProvider, TProvider>();
        if (typeof(TProvider).IsAssignableTo(typeof(ILifecycleParticipant<ISiloLifecycle>)))
        {
            services.AddFromExisting(typeof(ILifecycleParticipant<ISiloLifecycle>), typeof(TProvider));
        }

        return services;
    }
}