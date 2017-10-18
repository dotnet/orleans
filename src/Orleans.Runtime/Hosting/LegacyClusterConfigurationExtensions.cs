using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MembershipService;

namespace Orleans.Hosting
{
    internal static class LegacyClusterConfigurationExtensions
    {
        public static IServiceCollection AddLegacyClusterConfigurationSupport(this IServiceCollection services, ClusterConfiguration configuration)
        {
            if (services.Any(service => service.ServiceType == typeof(ClusterConfiguration)))
            {
                throw new InvalidOperationException("Cannot configure legacy ClusterConfiguration support twice");
            }

            // these will eventually be removed once our code doesn't depend on the old ClientConfiguration
            services.AddSingleton(configuration);
            services.TryAddSingleton<SiloInitializationParameters>();
            services.TryAddFromExisting<ILocalSiloDetails, SiloInitializationParameters>();
            services.TryAddSingleton(sp => sp.GetRequiredService<SiloInitializationParameters>().ClusterConfig);
            services.TryAddSingleton(sp => sp.GetRequiredService<SiloInitializationParameters>().ClusterConfig.Globals);
            services.TryAddTransient(sp => sp.GetRequiredService<SiloInitializationParameters>().NodeConfig);
            services.TryAddSingleton<Factory<NodeConfiguration>>(
                sp =>
                {
                    var initializationParams = sp.GetRequiredService<SiloInitializationParameters>();
                    return () => initializationParams.NodeConfig;
                });
            services.TryAddFromExisting<IMessagingConfiguration, GlobalConfiguration>();

            // Translate legacy configuration to new Options
            services.Configure<SiloMessagingOptions>(options =>
            {
                LegacyConfigurationExtensions.CopyCommonMessagingOptions(configuration.Globals, options);

                options.SiloSenderQueues = configuration.Globals.SiloSenderQueues;
                options.GatewaySenderQueues = configuration.Globals.GatewaySenderQueues;
                options.MaxForwardCount = configuration.Globals.MaxForwardCount;
                options.ClientDropTimeout = configuration.Globals.ClientDropTimeout;
            });

            services.Configure<SerializationProviderOptions>(options =>
            {
                options.SerializationProviders = configuration.Globals.SerializationProviders;
                options.FallbackSerializationProvider = configuration.Globals.FallbackSerializationProvider;
            });

            services.Configure<IOptions<SiloIdentityOptions>, GrainClassOptions>((identityOptions, options) =>
            {
                var nodeConfig = configuration.GetOrCreateNodeConfigurationForSilo(identityOptions.Value.SiloName);
                options.ExcludedGrainTypes.AddRange(nodeConfig.ExcludedGrainTypes);
            });

            LegacyMembershipConfigurator.ConfigureServices(configuration.Globals, services);
            return services;
        }

        internal static void Configure<TService, TOptions>(this IServiceCollection services, Action<TService, TOptions> configure) where TOptions : class
        {
            services.AddTransient<IConfigureOptions<TOptions>>(sp => new ServiceBasedConfigurator<TService, TOptions>(sp.GetRequiredService<TService>(), configure));
        }

        private class ServiceBasedConfigurator<TService, TOptions> : IConfigureOptions<TOptions> where TOptions : class
        {
            private readonly Action<TService, TOptions> configure;
            private readonly TService service;

            public ServiceBasedConfigurator(TService service, Action<TService, TOptions> configure)
            {
                this.service = service;
                this.configure = configure;
            }

            public void Configure(TOptions options) => this.configure(this.service, options);
        }
    }
}
