using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime;
using System.Diagnostics.CodeAnalysis;
using Orleans.Configuration.Internal;
using Orleans.Placement.Rebalancing;
using Orleans.Runtime.Placement.Rebalancing;

namespace Orleans.Hosting;

#nullable enable

/// <summary>
/// Extensions for configuring activation rebalancing.
/// </summary>
public static class ActivationRebalancerExtensions
{
    /// <summary>
    /// Enables activation rebalancing for the entire cluster.
    /// </summary>
    /// <remarks>
    /// Activation rebalancing attempts to distribute activations around the cluster in such a
    /// way that it optimizes both activation count and memory usages across the silos of the cluster.
    /// <para>You can read more on activation rebalancing <see href="https://www.ledjonbehluli.com/posts/orleans_adaptive_rebalancing/">here</see></para>
    /// </remarks>
    [Experimental("ORLEANSEXP002")]
    public static ISiloBuilder AddActivationRebalancer(this ISiloBuilder builder) =>
        builder.AddActivationRebalancer<FailedSessionBackoffProvider>();

    /// <inheritdoc cref="AddActivationRebalancer(ISiloBuilder)"/>.
    /// <typeparam name="TProvider">Custom backoff provider for determining next session after a failed attempt.</typeparam>
    [Experimental("ORLEANSEXP002")]
    public static ISiloBuilder AddActivationRebalancer<TProvider>(this ISiloBuilder builder)
        where TProvider : class, IFailedSessionBackoffProvider =>
        builder.ConfigureServices(service => service.AddActivationRebalancer<TProvider>());

    private static IServiceCollection AddActivationRebalancer<TProvider>(this IServiceCollection services)
        where TProvider : class, IFailedSessionBackoffProvider
    {
        services.AddSingleton<ActivationRebalancerMonitor>();
        services.AddFromExisting<IActivationRebalancer, ActivationRebalancerMonitor>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, ActivationRebalancerMonitor>();
        services.AddTransient<IConfigurationValidator, ActivationRebalancerOptionsValidator>();
        
        services.AddSingleton<TProvider>();
        services.AddFromExisting<IFailedSessionBackoffProvider, TProvider>();
        if (typeof(TProvider).IsAssignableTo(typeof(ILifecycleParticipant<ISiloLifecycle>)))
        {
            services.AddFromExisting(typeof(ILifecycleParticipant<ISiloLifecycle>), typeof(TProvider));
        }

        return services;
    }
}