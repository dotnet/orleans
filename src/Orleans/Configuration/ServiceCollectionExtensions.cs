﻿using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Configuration
{
    /// <summary>
    /// Extension methods for configuring dependency injection.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Attempts to use an existing registration of <typeparamref name="TImplementation"/> to satisfy the service type <typeparamref name="TService"/>.
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
        /// Attempts to use an existing registration of <typeparamref name="TImplementation"/> to satisfy the service type <typeparamref name="TService"/>.
        /// </summary>
        /// <typeparam name="TService">The service type being provided.</typeparam>
        /// <typeparam name="TImplementation">The implementation of <typeparamref name="TService"/>.</typeparam>
        /// <param name="services">The service collection.</param>
        internal static void TryAddFromExisting<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService
        {
            var implementation = services.FirstOrDefault(service => service.ServiceType == typeof(TImplementation));
            var providedService = services.FirstOrDefault(service => service.ServiceType == typeof(TService));
            if (providedService == null && implementation != null)
            {
                var newRegistration = new ServiceDescriptor(
                    typeof(TService),
                    sp => sp.GetRequiredService<TImplementation>(),
                    implementation.Lifetime);
                services.Add(newRegistration);
            }
        }

        /// <summary>
        /// Adds an <see cref="IGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="collection">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddGrainCallFilter(this IServiceCollection collection, IGrainCallFilter filter)
        {
            return collection.AddSingleton(filter);
        }

        /// <summary>
        /// Adds an <see cref="IGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="collection">The service collection.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddGrainCallFilter<TImplementation>(this IServiceCollection collection)
            where TImplementation : class, IGrainCallFilter
        {
            return collection.AddSingleton<IGrainCallFilter, TImplementation>();
        }

        /// <summary>
        /// Adds an <see cref="IGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="collection">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        public static IServiceCollection AddGrainCallFilter(this IServiceCollection collection, GrainCallFilterDelegate filter)
        {
            return collection.AddSingleton<IGrainCallFilter>(
                new GrainCallFilterWrapper(filter));
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
