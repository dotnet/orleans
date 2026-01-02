using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Configuration.Internal
{
    /// <summary>
    /// A <see cref="ServiceDescriptor"/> subclass that tracks the underlying implementation type
    /// used when registering a service via <see cref="ServiceCollectionExtensions.AddFromExisting"/>.
    /// This allows service registrations to be identified and removed later based on their implementation type.
    /// </summary>
    internal sealed class TaggedServiceDescriptor : ServiceDescriptor
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TaggedServiceDescriptor"/> class.
        /// </summary>
        /// <param name="serviceType">The type of the service.</param>
        /// <param name="factory">The factory used for creating service instances.</param>
        /// <param name="lifetime">The lifetime of the service.</param>
        /// <param name="implementationType">The underlying implementation type this registration was created from.</param>
        public TaggedServiceDescriptor(
            Type serviceType,
            Func<IServiceProvider, object> factory,
            ServiceLifetime lifetime,
            Type implementationType)
            : base(serviceType, factory, lifetime)
        {
            SourceImplementationType = implementationType;
        }

        /// <summary>
        /// Gets the underlying implementation type that this service registration was created from.
        /// </summary>
        public Type SourceImplementationType { get; }

        /// <summary>
        /// Removes all service descriptors from the collection that were registered from the specified implementation type.
        /// </summary>
        /// <typeparam name="TImplementation">The implementation type to remove registrations for.</typeparam>
        /// <param name="services">The service collection to remove from.</param>
        public static void RemoveAllForImplementation<TImplementation>(IServiceCollection services)
        {
            RemoveAllForImplementation(services, typeof(TImplementation));
        }

        /// <summary>
        /// Removes all service descriptors from the collection that were registered from the specified implementation type.
        /// </summary>
        /// <param name="services">The service collection to remove from.</param>
        /// <param name="implementationType">The implementation type to remove registrations for.</param>
        public static void RemoveAllForImplementation(IServiceCollection services, Type implementationType)
        {
            var toRemove = new List<ServiceDescriptor>();
            foreach (var descriptor in services)
            {
                if (descriptor is TaggedServiceDescriptor tagged && tagged.SourceImplementationType == implementationType)
                {
                    toRemove.Add(descriptor);
                }
                else if (descriptor.ServiceType == implementationType || descriptor.ImplementationType == implementationType)
                {
                    toRemove.Add(descriptor);
                }
            }

            foreach (var descriptor in toRemove)
            {
                services.Remove(descriptor);
            }
        }
    }

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
        public static void AddFromExisting<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService
        {
            services.AddFromExisting(typeof(TService), typeof(TImplementation));
        }

        /// <summary>
        /// Registers an existing registration of <paramref name="implementation"/> as a provider of service type <paramref name="service"/>.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="service">The service type being provided.</param>
        /// <param name="implementation">The implementation of <paramref name="service"/>.</param>
        public static void AddFromExisting(this IServiceCollection services, Type service, Type implementation)
        {
            ServiceDescriptor registration = null;
            foreach (var descriptor in services)
            {
                if (descriptor.ServiceType == implementation)
                {
                    registration = descriptor;
                    break;
                }
            }

            if (registration is null)
            {
                throw new ArgumentNullException(nameof(implementation), $"Unable to find previously registered ServiceType of '{implementation.FullName}'");
            }

            var newRegistration = new TaggedServiceDescriptor(
                service,
                sp => sp.GetRequiredService(implementation),
                registration.Lifetime,
                implementation);
            services.Add(newRegistration);
        }

        /// <summary>
        /// Registers an existing registration of <typeparamref name="TImplementation"/> as a provider of <typeparamref name="TService"/> if there are no existing <typeparamref name="TService"/> implementations.
        /// </summary>
        /// <typeparam name="TService">The service type being provided.</typeparam>
        /// <typeparam name="TImplementation">The implementation of <typeparamref name="TService"/>.</typeparam>
        /// <param name="services">The service collection.</param>
        public static void TryAddFromExisting<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService
        {
            if (services.All(service => service.ServiceType != typeof(TService)))
            {
                services.AddFromExisting<TService, TImplementation>();
            }
        }
    }
}
