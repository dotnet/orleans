using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.BroadcastChannel;
using Orleans.BroadcastChannel.SubscriberTable;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    public static class ChannelHostingExtensions
    {
        /// <summary>
        /// Add a new broadcast channel to the silo.
        /// </summary>
        /// <param name="this">The builder.</param>
        /// <param name="name">The name of the provider</param>
        /// <param name="configureOptions">The configuration delegate.</param>
        /// <returns></returns>
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
        public static ISiloBuilder AddBroadcastChannel(this ISiloBuilder @this, string name, Action<OptionsBuilder<BroadcastChannelOptions>> configureOptions = null)
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
        public static IClientBuilder AddBroadcastChannel(this IClientBuilder @this, string name, Action<OptionsBuilder<BroadcastChannelOptions>> configureOptions = null)
        {
            @this.Services.AddBroadcastChannel(name, configureOptions);
            return @this;
        }

        /// <summary>
        /// Get the named broadcast channel provided.
        /// </summary>
        /// <param name="this">The client.</param>
        /// <param name="name">The name of the provider</param>
        public static IBroadcastChannelProvider GetBroadcastChannelProvider(this IClusterClient @this, string name)
            => @this.ServiceProvider.GetRequiredServiceByName<IBroadcastChannelProvider>(name);

        private static void AddBroadcastChannel(this IServiceCollection services, string name, Action<OptionsBuilder<BroadcastChannelOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<BroadcastChannelOptions>(name));
            services.ConfigureNamedOptionForLogging<BroadcastChannelOptions>(name);
            services
                .AddSingleton<ImplicitChannelSubscriberTable>()
                .AddSingleton<IChannelNamespacePredicateProvider, DefaultChannelNamespacePredicateProvider>()
                .AddSingleton<IChannelNamespacePredicateProvider, ConstructorChannelNamespacePredicateProvider>()
                .AddSingletonKeyedService<string, IChannelIdMapper, DefaultChannelIdMapper>(DefaultChannelIdMapper.Name)
                .AddSingletonNamedService(name, BroadcastChannelProvider.Create);
        }
    }
}
