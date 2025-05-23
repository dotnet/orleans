using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Runtime;
using Orleans.Configuration;
using Orleans.Runtime.Placement.Repartitioning;
using System.Diagnostics.CodeAnalysis;
using Orleans.Placement.Repartitioning;

namespace Orleans.Hosting;

#nullable enable
public static class ActivationRepartitioningExtensions
{
    /// <summary>
    /// Enables activation repartitioning for this silo.
    /// </summary>
    /// <remarks>
    /// Activation repartitioning attempts to optimize grain call locality by collocating activations which communicate frequently,
    /// while keeping the number of activations on each silo approximately equal.
    /// </remarks>
    [Experimental("ORLEANSEXP001")]
    public static ISiloBuilder AddActivationRepartitioner(this ISiloBuilder builder)
        => builder.AddActivationRepartitioner<RebalancerCompatibleRule>();

    /// <summary>
    /// Enables activation repartitioning for this silo.
    /// </summary>
    /// <remarks>
    /// Activation repartitioning attempts to optimize grain call locality by collocating activations which communicate frequently,
    /// while keeping the number of activations on each silo approximately equal.
    /// </remarks>
    /// <typeparam name="TRule">The type of the imbalance rule to use.</typeparam>
    [Experimental("ORLEANSEXP001")]
    public static ISiloBuilder AddActivationRepartitioner<TRule>(this ISiloBuilder builder) where TRule : class, IImbalanceToleranceRule
        => builder
            .ConfigureServices(services => services.AddActivationRepartitioner<TRule>());

    private static IServiceCollection AddActivationRepartitioner<TRule>(this IServiceCollection services) where TRule : class, IImbalanceToleranceRule
    {
        services.AddSingleton<ActivationRepartitioner>();
        services.AddSingleton<IRepartitionerMessageFilter, RepartitionerMessageFilter>();
        services.AddFromExisting<IMessageStatisticsSink, ActivationRepartitioner>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, ActivationRepartitioner>();
        services.AddTransient<IConfigurationValidator, ActivationRepartitionerOptionsValidator>();

        services.AddSingleton<TRule>();
        services.AddFromExisting<IImbalanceToleranceRule, TRule>();
        if (typeof(TRule).IsAssignableTo(typeof(ILifecycleParticipant<ISiloLifecycle>)))
        {
            services.AddFromExisting(typeof(ILifecycleParticipant<ISiloLifecycle>), typeof(TRule));
        }

        return services;
    }
}
