using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime.Configuration;
using System;

namespace Orleans.Configuration
{
    public static class LegacyConfigurationExtensions
    {
        public static IServiceCollection AddLegacySiloConfigurationSupport(this IServiceCollection services)
        {
            // Global configuration

            services.Configure<ClusterConfiguration, SiloMessagingOptions>((configuration, options) =>
            {
                CopyCommonMessagingOptions(configuration.Globals, options);

                options.SiloSenderQueues = configuration.Globals.SiloSenderQueues;
                options.GatewaySenderQueues = configuration.Globals.GatewaySenderQueues;
                options.MaxForwardCount = configuration.Globals.MaxForwardCount;
                options.ClientDropTimeout = configuration.Globals.ClientDropTimeout;
            });

            services.Configure<ClusterConfiguration, SerializationProviderOptions>((configuration, options) =>
            {
                options.SerializationProviders = configuration.Globals.SerializationProviders;
                options.FallbackSerializationProvider = configuration.Globals.FallbackSerializationProvider;
            });

            return services;
        }

        public static IServiceCollection AddLegacyClientConfigurationSupport(this IServiceCollection services)
        {
            // Global configuration

            services.Configure<ClientConfiguration, ClientMessagingOptions>((configuration, options) =>
            {
                CopyCommonMessagingOptions(configuration, options);

                options.ClientSenderBuckets = configuration.ClientSenderBuckets;
            });

            services.Configure<ClientConfiguration, SerializationProviderOptions>((configuration, options) =>
            {
                options.SerializationProviders = configuration.SerializationProviders;
                options.FallbackSerializationProvider = configuration.FallbackSerializationProvider;
            });

            return services;
        }

        private static void CopyCommonMessagingOptions(IMessagingConfiguration configuration, MessagingOptions options)
        {
            options.OpenConnectionTimeout = configuration.OpenConnectionTimeout;
            options.ResponseTimeout = configuration.ResponseTimeout;
            options.MaxResendCount = configuration.MaxResendCount;
            options.ResendOnTimeout = configuration.ResendOnTimeout;
            options.MaxSocketAge = configuration.MaxSocketAge;
            options.DropExpiredMessages = configuration.DropExpiredMessages;
            options.BufferPoolBufferSize = configuration.BufferPoolBufferSize;
            options.BufferPoolMaxSize = configuration.BufferPoolMaxSize;
            options.BufferPoolPreallocationSize = configuration.BufferPoolPreallocationSize;
        }

        private static void Configure<TConfiguration, TOptions>(this IServiceCollection services, Action<TConfiguration, TOptions> configureOptions) where TOptions : class
        {
            services.AddSingleton<IConfigureOptions<TOptions>>(serviceProvider =>
            {
                var configuration = serviceProvider.GetRequiredService<TConfiguration>();

                return new ConfigureOptions<TOptions>(options => configureOptions(configuration, options));
            });
        }
    }
}
