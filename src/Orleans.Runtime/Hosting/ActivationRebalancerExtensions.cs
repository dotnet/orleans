using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Placement;
using Orleans.Runtime;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Options;
using System;
using Orleans.Configuration.Internal;
using Orleans.Internal;

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
        builder.AddActivationRebalancer<FixedBackoffProvider>();

    /// <inheritdoc cref="AddActivationRebalancer(ISiloBuilder)"/>.
    /// <typeparam name="TProvider">Custom backoff provider for determining next session after a failed attempt.</typeparam>
    [Experimental("ORLEANSEXP002")]
    public static ISiloBuilder AddActivationRebalancer<TProvider>(this ISiloBuilder builder)
        where TProvider : class, IFailedRebalancingSessionBackoffProvider
    {
        builder.AddStartupTask<StartupTask>();
        builder.Services.AddSingleton<TProvider>();
        builder.Services.AddFromExisting<IFailedRebalancingSessionBackoffProvider, TProvider>();
        builder.Services.AddTransient<IConfigurationValidator, ActivationRebalancerOptionsValidator>();
        
        return builder;
    }

    private sealed class StartupTask(IGrainFactory grainFactory) : IStartupTask
    {
        public Task Execute(CancellationToken cancellationToken) =>
            grainFactory.GetGrain<IInternalActivationRebalancerGrain>(
                IActivationRebalancerGrain.Key).StartRebalancer();
    }

    private sealed class FixedBackoffProvider(IOptions<ActivationRebalancerOptions> options) :
        FixedBackoff(options.Value.SessionCyclePeriod), IFailedRebalancingSessionBackoffProvider;
}