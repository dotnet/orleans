using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.BroadcastChannel;
using Orleans.BroadcastChannel.SubscriberTable;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    public static class ChannelHostingExtensions
    {
        public static ISiloBuilder AddBroadcastChannel(this ISiloBuilder @this, string name, Action<BroadcastChannelOptions> configureOptions)
        {
            @this.Services.AddBroadcastChannel(name, ob => ob.Configure(configureOptions));
            return @this;
        }

        public static ISiloBuilder AddBroadcastChannel(this ISiloBuilder @this, string name, Action<OptionsBuilder<BroadcastChannelOptions>> configureOptions = null)
        {
            @this.Services.AddBroadcastChannel(name, configureOptions);
            @this.AddGrainExtension<IBroadcastChannelConsumerExtension, BroadcastChannelConsumerExtension>();
            return @this;
        }

        public static IClientBuilder AddBroadcastChannel(this IClientBuilder @this, string name, Action<BroadcastChannelOptions> configureOptions)
        {
            @this.Services.AddBroadcastChannel(name, ob => ob.Configure(configureOptions));
            return @this;
        }

        public static IClientBuilder AddBroadcastChannel(this IClientBuilder @this, string name, Action<OptionsBuilder<BroadcastChannelOptions>> configureOptions = null)
        {
            @this.Services.AddBroadcastChannel(name, configureOptions);
            return @this;
        }

        public static IBroadcastChannelProvider GetBroadcaseChannelProvider(this IClusterClient @this, string name)
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
