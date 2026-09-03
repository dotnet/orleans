using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.BroadcastChannel;
using Orleans.BroadcastChannel.SubscriberTable;
using Orleans.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Provides extension methods for configuring and resolving broadcast channel providers.
    /// </summary>
    public static class ChannelHostingExtensions
    {
        /// <summary>
        /// Add a new broadcast channel to the silo.
        /// </summary>
        /// <param name="this">The builder.</param>
        /// <param name="name">The name of the provider</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddBroadcastChannel(this ISiloBuilder @this, string name, Action<BroadcastChannelOptions> configureOptions)
        {
            @this.Services.AddBroadcastChannel(name, ob => ob.Configure(configureOptions));
            @this.AddGrainExtension<IBroadcastChannelConsumerExtension, BroadcastChannelConsumerExtension>();
            return @this;
        }

        /// <summary>
        /// Add a new broadcast channel to the silo.
        /// </summary>
        /// <param name="this">The builder.</param>
        /// <param name="name">The name of the provider</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddBroadcastChannel(this ISiloBuilder @this, string name, Action<OptionsBuilder<BroadcastChannelOptions>>? configureOptions = null)
        {
            @this.Services.AddBroadcastChannel(name, configureOptions);
            @this.AddGrainExtension<IBroadcastChannelConsumerExtension, BroadcastChannelConsumerExtension>();
            return @this;
        }

        /// <summary>
        /// Add a new broadcast channel to the client.
        /// </summary>
        /// <param name="this">The builder.</param>
        /// <param name="name">The name of the provider</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddBroadcastChannel(this IClientBuilder @this, string name, Action<BroadcastChannelOptions> configureOptions)
        {
            @this.Services.AddBroadcastChannel(name, ob => ob.Configure(configureOptions));
            return @this;
        }

        /// <summary>
        /// Add a new broadcast channel to the client.
        /// </summary>
        /// <param name="this">The builder.</param>
        /// <param name="name">The name of the provider</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddBroadcastChannel(this IClientBuilder @this, string name, Action<OptionsBuilder<BroadcastChannelOptions>>? configureOptions = null)
        {
            @this.Services.AddBroadcastChannel(name, configureOptions);
            return @this;
        }

        /// <summary>
        /// Gets a named broadcast channel provider.
        /// </summary>
        /// <param name="this">The client.</param>
        /// <param name="name">The name of the provider</param>
        /// <returns>The named broadcast channel provider.</returns>
        public static IBroadcastChannelProvider GetBroadcastChannelProvider(this IClusterClient @this, string name)
            => @this.ServiceProvider.GetRequiredKeyedService<IBroadcastChannelProvider>(name);

        private static void AddBroadcastChannel(this IServiceCollection services, string name, Action<OptionsBuilder<BroadcastChannelOptions>>? configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<BroadcastChannelOptions>(name));
            services.ConfigureNamedOptionForLogging<BroadcastChannelOptions>(name);
            services
                .AddSingleton<ImplicitChannelSubscriberTable>()
                .AddSingleton<IChannelNamespacePredicateProvider, DefaultChannelNamespacePredicateProvider>()
                .AddSingleton<IChannelNamespacePredicateProvider, ConstructorChannelNamespacePredicateProvider>()
                .AddKeyedSingleton<IChannelIdMapper, DefaultChannelIdMapper>(DefaultChannelIdMapper.Name)
                .AddKeyedSingleton(name, (sp, key) => BroadcastChannelProvider.Create(sp, (key as string)!));
        }
    }
}
