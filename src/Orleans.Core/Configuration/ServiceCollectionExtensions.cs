using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using System;
using Orleans.Configuration.Options;
using Orleans.Hosting;
using Orleans.Messaging;

namespace Orleans.Configuration
{
    /// <summary>
    /// Extension methods for configuring dependency injection.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers an existing registration of <typeparamref name="TImplementation"/> as a provider of service type <typeparamref name="TService"/>.
        /// </summary>
        /// <typeparam name="TService">The service type being provided.</typeparam>
        /// <typeparam name="TImplementation">The implementation of <typeparamref name="TService"/>.</typeparam>
        /// <param name="services">The service collection.</param>
        internal static void AddFromExisting<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService
        {
            var registration = services.FirstOrDefault(service => service.ServiceType == typeof(TImplementation));
            if (registration != null)
            {
                var newRegistration = new ServiceDescriptor(
                    typeof(TService),
                    sp => sp.GetRequiredService<TImplementation>(),
                    registration.Lifetime);
                services.Add(newRegistration);
            }
        }

        /// <summary>
        /// Registers an existing registration of <typeparamref name="TImplementation"/> as a provider of <typeparamref name="TService"/> if there are no existing <typeparamref name="TService"/> implementations.
        /// </summary>
        /// <typeparam name="TService">The service type being provided.</typeparam>
        /// <typeparam name="TImplementation">The implementation of <typeparamref name="TService"/>.</typeparam>
        /// <param name="services">The service collection.</param>
        internal static void TryAddFromExisting<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService
        {
            var providedService = services.FirstOrDefault(service => service.ServiceType == typeof(TService));
            if (providedService == null)
            {
                services.AddFromExisting<TService, TImplementation>();
            }
        }

        /// <summary>
        /// Adds an <see cref="IGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddGrainCallFilter(this IServiceCollection services, IGrainCallFilter filter)
        {
            return services.AddSingleton(filter);
        }

        /// <summary>
        /// Adds an <see cref="IGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddGrainCallFilter<TImplementation>(this IServiceCollection services)
            where TImplementation : class, IGrainCallFilter
        {
            return services.AddSingleton<IGrainCallFilter, TImplementation>();
        }

        /// <summary>
        /// Adds an <see cref="IGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddGrainCallFilter(this IServiceCollection services, GrainCallFilterDelegate filter)
        {
            return services.AddSingleton<IGrainCallFilter>(
                new GrainCallFilterWrapper(filter));
        }

        /// <summary>
        /// Add an <see cref="StaticGatewayListProvider"/> and configure it using <paramref name="configureOptions"/>
        /// </summary>
        public static IServiceCollection UseStaticGatewayListProvider(this IServiceCollection services,
            Action<StaticGatewayListProviderOptions> configureOptions)
        {
            return services.UseStaticGatewayListProvider(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Add an <see cref="StaticGatewayListProvider"/> and configure it using <paramref name="configureOptions"/>
        /// </summary>
        public static IServiceCollection UseStaticGatewayListProvider(this IServiceCollection services,
            Action<OptionsBuilder<StaticGatewayListProviderOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<StaticGatewayListProviderOptions>());
            return services.AddSingleton<IGatewayListProvider, StaticGatewayListProvider>()
                .TryConfigureFormatter<StaticGatewayListProviderOptions,StaticGatewayListProviderOptionsFormatter>();
        }

        public static bool IsInCollection<T>(this IServiceCollection services)
        {
            return services.Any(s => s.ServiceType == typeof(T));
        }


        private class GrainCallFilterWrapper : IGrainCallFilter
        {
            private readonly GrainCallFilterDelegate interceptor;

            public GrainCallFilterWrapper(GrainCallFilterDelegate interceptor)
            {
                this.interceptor = interceptor;
            }

            public Task Invoke(IGrainCallContext context) => this.interceptor.Invoke(context);
        }
    }
}
