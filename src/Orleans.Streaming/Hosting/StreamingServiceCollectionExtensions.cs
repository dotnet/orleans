using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Providers;
using Orleans.Serialization;
using Orleans.Streaming.JsonConverters;
using Orleans.Streams;
using Orleans.Streams.Core;
using Orleans.Streams.Filtering;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for configuring streaming on silos.
    /// </summary>
    public static class StreamingServiceCollectionExtensions
    {
        /// <summary>
        /// Add support for streaming to this silo.
        /// </summary>
        /// <param name="services">The services.</param>
        public static void AddSiloStreaming(this IServiceCollection services)
        {
            if (services.Any(service => service.ServiceType.Equals(typeof(SiloStreamProviderRuntime))))
            {
                return;
            }

            services.AddSingleton<PubSubGrainStateStorageFactory>();
            services.AddSingleton<SiloStreamProviderRuntime>();
            services.AddFromExisting<IStreamProviderRuntime, SiloStreamProviderRuntime>();
            services.AddSingleton<ImplicitStreamSubscriberTable>();
            services.AddSingleton<IConfigureGrainContext, StreamConsumerGrainContextAction>();
            services.AddSingleton<IStreamNamespacePredicateProvider, DefaultStreamNamespacePredicateProvider>();
            services.AddSingleton<IStreamNamespacePredicateProvider, ConstructorStreamNamespacePredicateProvider>();
            services.AddSingletonKeyedService<string, IStreamIdMapper, DefaultStreamIdMapper>(DefaultStreamIdMapper.Name);
            services.AddTransientKeyedService<Type, IGrainExtension>(typeof(IStreamConsumerExtension), (sp, _) =>
            {
                var runtime = sp.GetRequiredService<IStreamProviderRuntime>();
                var grainContextAccessor = sp.GetRequiredService<IGrainContextAccessor>();
                return new StreamConsumerExtension(runtime, grainContextAccessor.GrainContext?.GrainInstance as IStreamSubscriptionObserver);
            });
            services.AddSingleton<IStreamSubscriptionManagerAdmin>(sp =>
                new StreamSubscriptionManagerAdmin(sp.GetRequiredService<IStreamProviderRuntime>()));
            services.AddTransient<IStreamQueueBalancer, ConsistentRingQueueBalancer>();
            services.AddSingleton<IPostConfigureOptions<OrleansJsonSerializerOptions>, StreamingConverterConfigurator>();

            // One stream directory per activation
            services.AddScoped<StreamDirectory>();
        }

        /// <summary>
        /// Add support for streaming to this client.
        /// </summary>
        /// <param name="services">The services.</param>
        public static void AddClientStreaming(this IServiceCollection services)
        {
            if (services.Any(service => service.ServiceType.Equals(typeof(ClientStreamingProviderRuntime))))
            {
                return;
            }

            services.AddSingleton<ClientStreamingProviderRuntime>();
            services.AddFromExisting<IStreamProviderRuntime, ClientStreamingProviderRuntime>();
            services.AddSingleton<IStreamSubscriptionManagerAdmin, StreamSubscriptionManagerAdmin>();
            services.AddSingleton<ImplicitStreamSubscriberTable>();
            services.AddSingleton<IStreamNamespacePredicateProvider, DefaultStreamNamespacePredicateProvider>();
            services.AddSingleton<IStreamNamespacePredicateProvider, ConstructorStreamNamespacePredicateProvider>();
            services.AddSingletonKeyedService<string, IStreamIdMapper, DefaultStreamIdMapper>(DefaultStreamIdMapper.Name);
            services.AddFromExisting<ILifecycleParticipant<IClusterClientLifecycle>, ClientStreamingProviderRuntime>();
            services.AddSingleton<IPostConfigureOptions<OrleansJsonSerializerOptions>, StreamingConverterConfigurator>();
        }

        /// <summary>
        /// Adds a stream filter. 
        /// </summary>
        /// <typeparam name="T">The stream filter type.</typeparam>
        /// <param name="services">The service collection.</param>
        /// <param name="name">The stream filter name.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddStreamFilter<T>(this IServiceCollection services, string name) where T : class, IStreamFilter
        {
            return services.AddSingletonNamedService<IStreamFilter, T>(name);
        }
    }
}
