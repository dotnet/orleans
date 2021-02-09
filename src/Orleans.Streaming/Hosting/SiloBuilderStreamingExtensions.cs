using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Providers;
using Orleans.Streams;
using Orleans.Streams.Core;
using Orleans.Streams.Filtering;

namespace Orleans.Hosting
{
    public static class SiloBuilderStreamingExtensions
    {
        /// <summary>
        /// Add support for streaming to this application.
        /// </summary>
        public static ISiloHostBuilder AddStreaming(this ISiloHostBuilder builder) => builder.ConfigureServices(AddSiloStreaming);

        /// <summary>
        /// Add support for streaming to this application.
        /// </summary>
        public static ISiloBuilder AddStreaming(this ISiloBuilder builder) => builder.ConfigureServices(AddSiloStreaming);

        /// <summary>
        /// Add support for streaming to this silo.
        /// </summary>
        public static void AddSiloStreaming(this IServiceCollection services)
        {
            if (services.Any(service => service.ServiceType.Equals(typeof(SiloStreamProviderRuntime))))
            {
                return;
            }

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
                return new StreamConsumerExtension(runtime, (IStreamSubscriptionObserver) grainContextAccessor.GrainContext?.GrainInstance);
            });
            services.AddSingleton<IStreamSubscriptionManagerAdmin>(sp =>
                new StreamSubscriptionManagerAdmin(sp.GetRequiredService<IStreamProviderRuntime>()));
            services.AddTransient<IStreamQueueBalancer, ConsistentRingQueueBalancer>();

            // One stream directory per activation
            services.AddScoped<StreamDirectory>();
        }

        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static ISiloHostBuilder AddPersistentStreams(this ISiloHostBuilder builder, string name,
            Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory,
            Action<ISiloPersistentStreamConfigurator> configureStream)
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var streamConfigurator = new SiloPersistentStreamConfigurator(name, configureDelegate => builder.ConfigureServices(configureDelegate), adapterFactory);
            configureStream?.Invoke(streamConfigurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use SimpleMessageProvider
        /// </summary>
        public static ISiloHostBuilder AddSimpleMessageStreamProvider(
            this ISiloHostBuilder builder,
            string name,
            Action<ISimpleMessageStreamConfigurator> configureStream)
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var streamConfigurator = new SimpleMessageStreamConfigurator(name, configureDelegate => builder.ConfigureServices(configureDelegate), builder);
            configureStream?.Invoke(streamConfigurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use SimpleMessageProvider
        /// </summary>
        public static ISiloHostBuilder AddSimpleMessageStreamProvider(this ISiloHostBuilder builder, string name,
            Action<SimpleMessageStreamProviderOptions> configureOptions)
        {
            return AddSimpleMessageStreamProvider(builder, name, b => b
                .Configure<SimpleMessageStreamProviderOptions>(ob => ob.Configure(configureOptions)));
        }

        /// <summary>
        /// Configure silo to use SimpleMessageProvider
        /// </summary>
        public static ISiloHostBuilder AddSimpleMessageStreamProvider(this ISiloHostBuilder builder, string name,
            Action<OptionsBuilder<SimpleMessageStreamProviderOptions>> configureOptions = null)
        {
            return AddSimpleMessageStreamProvider(builder, name, b => b.Configure(configureOptions));
        }

        /// <summary>
        /// Configure silo to use persistent streams.
        /// </summary>
        public static ISiloBuilder AddPersistentStreams(this ISiloBuilder builder, string name,
            Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory,
            Action<ISiloPersistentStreamConfigurator> configureStream)
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var streamConfigurator = new SiloPersistentStreamConfigurator(name, configureDelegate => builder.ConfigureServices(configureDelegate), adapterFactory);
            configureStream?.Invoke(streamConfigurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use SimpleMessageProvider
        /// </summary>
        public static ISiloBuilder AddSimpleMessageStreamProvider(
            this ISiloBuilder builder,
            string name,
            Action<ISimpleMessageStreamConfigurator> configureStream)
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var streamConfigurator = new SimpleMessageStreamConfigurator(name, configureDelegate => builder.ConfigureServices(configureDelegate), builder);
            configureStream?.Invoke(streamConfigurator);
            return builder;
        }

        /// <summary>
        /// Configure silo to use SimpleMessageProvider
        /// </summary>
        public static ISiloBuilder AddSimpleMessageStreamProvider(this ISiloBuilder builder, string name,
            Action<SimpleMessageStreamProviderOptions> configureOptions)
        {
            return AddSimpleMessageStreamProvider(builder, name, b => b
                .Configure<SimpleMessageStreamProviderOptions>(ob => ob.Configure(configureOptions)));
        }

        /// <summary>
        /// Configure silo to use SimpleMessageProvider
        /// </summary>
        public static ISiloBuilder AddSimpleMessageStreamProvider(this ISiloBuilder builder, string name,
            Action<OptionsBuilder<SimpleMessageStreamProviderOptions>> configureOptions = null)
        {
            return AddSimpleMessageStreamProvider(builder, name, b => b.Configure(configureOptions));
        }

        public static ISiloBuilder AddStreamFilter<T>(this ISiloBuilder builder, string name) where T : class, IStreamFilter
        {
            return builder.ConfigureServices(svc => svc.AddStreamFilter<T>(name));
        }

        public static ISiloHostBuilder AddStreamFilter<T>(this ISiloHostBuilder builder, string name) where T : class, IStreamFilter
        {
            return builder.ConfigureServices(svc => svc.AddStreamFilter<T>(name));
        }

        public static IClientBuilder AddStreamFilter<T>(this IClientBuilder builder, string name) where T : class, IStreamFilter
        {
            return builder.ConfigureServices(svc => svc.AddStreamFilter<T>(name));
        }

        public static IServiceCollection AddStreamFilter<T>(this IServiceCollection services, string name) where T : class, IStreamFilter
        {
            return services.AddSingletonNamedService<IStreamFilter, T>(name);
        }
    }
}
