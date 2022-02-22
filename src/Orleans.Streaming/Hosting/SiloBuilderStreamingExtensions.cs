using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Providers.Streams.SimpleMessageStream;
using Orleans.Runtime;
using Orleans.Runtime.Providers;
using Orleans.Streams;
using Orleans.Streams.Core;
using Orleans.Streams.Filtering;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extension methods for confiiguring streaming on silos.
    /// </summary>
    public static class SiloBuilderStreamingExtensions
    {
        /// <summary>
        /// Add support for streaming to this application.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddStreaming(this ISiloBuilder builder) => builder.ConfigureServices(AddSiloStreaming);

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

            // One stream directory per activation
            services.AddScoped<StreamDirectory>();
        }

        /// <summary>
        /// Configures the silo to use persistent streams.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The provider name.</param>
        /// <param name="adapterFactory">The provider adapter factory.</param>
        /// <param name="configureStream">The stream provider configuration delegate.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddPersistentStreams(
            this ISiloBuilder builder,
            string name,
            Func<IServiceProvider, string, IQueueAdapterFactory> adapterFactory,
            Action<ISiloPersistentStreamConfigurator> configureStream)
        {
            //the constructor wire up DI with all default components of the streams , so need to be called regardless of configureStream null or not
            var streamConfigurator = new SiloPersistentStreamConfigurator(name, configureDelegate => builder.ConfigureServices(configureDelegate), adapterFactory);
            configureStream?.Invoke(streamConfigurator);
            return builder;
        }

        /// <summary>
        /// Adds a new <see cref="SimpleMessageStreamProvider"/> stream provider.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The provider name.</param>
        /// <param name="configureStream">The stream provider configuration delegate.</param>
        /// <returns>The silo builder.</returns>
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
        /// Adds a new <see cref="SimpleMessageStreamProvider"/> stream provider.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The provider name.</param>
        /// <param name="configureOptions">The delegate used to configure the stream provider options.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddSimpleMessageStreamProvider(
            this ISiloBuilder builder,
            string name,
            Action<SimpleMessageStreamProviderOptions> configureOptions)
        {
            return AddSimpleMessageStreamProvider(builder, name, b => b
                .Configure<SimpleMessageStreamProviderOptions>(ob => ob.Configure(configureOptions)));
        }

        /// <summary>
        /// Adds a new <see cref="SimpleMessageStreamProvider"/> stream provider.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The provider name.</param>
        /// <param name="configureOptions">The delegate used to configure the stream provider options.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddSimpleMessageStreamProvider(
            this ISiloBuilder builder,
            string name,
            Action<OptionsBuilder<SimpleMessageStreamProviderOptions>> configureOptions = null)
        {
            return AddSimpleMessageStreamProvider(builder, name, b => b.Configure(configureOptions));
        }

        /// <summary>
        /// Adds a stream filter. 
        /// </summary>
        /// <typeparam name="T">The stream filter type.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The stream filter name.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddStreamFilter<T>(this ISiloBuilder builder, string name) where T : class, IStreamFilter
        {
            return builder.ConfigureServices(svc => svc.AddStreamFilter<T>(name));
        }

        /// <summary>
        /// Adds a stream filter. 
        /// </summary>
        /// <typeparam name="T">The stream filter type.</typeparam>
        /// <param name="builder">The builder.</param>
        /// <param name="name">The stream filter name.</param>
        /// <returns>The client builder.</returns>
        public static IClientBuilder AddStreamFilter<T>(this IClientBuilder builder, string name) where T : class, IStreamFilter
        {
            return builder.ConfigureServices(svc => svc.AddStreamFilter<T>(name));
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
