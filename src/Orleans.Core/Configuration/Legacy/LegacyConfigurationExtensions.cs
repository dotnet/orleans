using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace Orleans.Configuration
{
    internal static class LegacyConfigurationExtensions
    {
        public static IServiceCollection AddLegacyClientConfigurationSupport(this IServiceCollection services, ClientConfiguration configuration)
        {
            if (services.Any(service => service.ServiceType == typeof(ClientConfiguration)))
            {
                throw new InvalidOperationException("Cannot configure legacy ClientConfiguration support twice");
            }

            // these will eventually be removed once our code doesn't depend on the old ClientConfiguration
            services.TryAddSingleton(configuration);
            services.TryAddFromExisting<IMessagingConfiguration, ClientConfiguration>();

            // Translate legacy configuration to new Options
            services.Configure<ClientMessagingOptions>(options =>
            {
                CopyCommonMessagingOptions(configuration, options);

                options.ClientSenderBuckets = configuration.ClientSenderBuckets;
            });

            services.Configure<SerializationProviderOptions>(options =>
            {
                options.SerializationProviders = configuration.SerializationProviders;
                options.FallbackSerializationProvider = configuration.FallbackSerializationProvider;
            });

            // GatewayProvider
            LegacyGatewayListProviderConfigurator.ConfigureServices(configuration, services);
            return services;
        }

        internal static void CopyCommonMessagingOptions(IMessagingConfiguration configuration, MessagingOptions options)
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
    }
}
