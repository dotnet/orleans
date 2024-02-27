using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Placement.Rebalancing;
using Orleans.Runtime;
using Orleans.Runtime.Configuration.Options;
using Orleans.Runtime.Placement.Rebalancing;

namespace Orleans.Hosting;

#nullable enable
public static class ActiveRebalancingExtensions
{
    /// <summary>
    /// Adds support for active-rebalancing in this silo.
    /// </summary>
    public static ISiloBuilder AddActiveRebalancing(this ISiloBuilder builder)
        => builder.AddActiveRebalancing<DefaultImbalanceRule>();

    /// <summary>
    /// Adds support for active-rebalancing in this silo.
    /// </summary>
    public static ISiloBuilder AddActiveRebalancing<TRule>(this ISiloBuilder builder) where TRule : class, IImbalanceToleranceRule
        => builder
            .AddGrainExtension<IActiveRebalancerExtension, ActiveRebalancerExtension>()
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
        services.AddSingleton<ActiveRebalancerGateway>();
        services.AddFromExisting<IActiveRebalancerGateway, ActiveRebalancerGateway>();
        services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, ActiveRebalancerGateway>();

        return services;
    }
}