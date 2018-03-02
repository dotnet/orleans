using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using System;
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
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        [Obsolete("Use " + nameof(AddIncomingGrainCallFilter))]
        public static IServiceCollection AddGrainCallFilter(this IServiceCollection services, IIncomingGrainCallFilter filter)
        {
            return services.AddSingleton(filter);
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection.</returns>
        [Obsolete("Use " + nameof(AddIncomingGrainCallFilter))]

        public static IServiceCollection AddGrainCallFilter<TImplementation>(this IServiceCollection services)
            where TImplementation : class, IIncomingGrainCallFilter
        {
            return services.AddSingleton<IIncomingGrainCallFilter, TImplementation>();
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        [Obsolete("Use " + nameof(AddIncomingGrainCallFilter))]
        public static IServiceCollection AddGrainCallFilter(this IServiceCollection services, GrainCallFilterDelegate filter)
        {
            return AddIncomingGrainCallFilter(services, filter);
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddIncomingGrainCallFilter(this IServiceCollection services, IIncomingGrainCallFilter filter)
        {
            return services.AddSingleton(filter);
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddIncomingGrainCallFilter<TImplementation>(this IServiceCollection services)
            where TImplementation : class, IIncomingGrainCallFilter
        {
            return services.AddSingleton<IIncomingGrainCallFilter, TImplementation>();
        }

        /// <summary>
        /// Adds an <see cref="IIncomingGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddIncomingGrainCallFilter(this IServiceCollection services, GrainCallFilterDelegate filter)
        {
            return services.AddSingleton<IIncomingGrainCallFilter>(new GrainCallFilterWrapper(filter));
        }

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddOutgoingGrainCallFilter(this IServiceCollection services, IOutgoingGrainCallFilter filter)
        {
            return services.AddSingleton(filter);
        }

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline.
        /// </summary>
        /// <typeparam name="TImplementation">The filter implementation type.</typeparam>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddOutgoingGrainCallFilter<TImplementation>(this IServiceCollection services)
            where TImplementation : class, IOutgoingGrainCallFilter
        {
            return services.AddSingleton<IOutgoingGrainCallFilter, TImplementation>();
        }

        /// <summary>
        /// Adds an <see cref="IOutgoingGrainCallFilter"/> to the filter pipeline via a delegate.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="filter">The filter.</param>
        /// <returns>The service collection.</returns>
        internal static IServiceCollection AddOutgoingGrainCallFilter(this IServiceCollection services, GrainCallFilterDelegate filter)
        {
            return services.AddSingleton<IOutgoingGrainCallFilter>(new GrainCallFilterWrapper(filter));
        }

        private class GrainCallFilterWrapper : IIncomingGrainCallFilter, IOutgoingGrainCallFilter
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
