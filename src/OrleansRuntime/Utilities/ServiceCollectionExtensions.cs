using System.Linq;

using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Runtime
{
    /// <summary>
    /// Extension methods for <see cref="IServiceCollection"/>.
    /// </summary>
    internal static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Attempts to use an existing registration of <typeparamref name="TImplementation"/> to satisfy the service type <typeparamref name="TService"/>.
        /// </summary>
        /// <typeparam name="TService">The service type being provided.</typeparam>
        /// <typeparam name="TImplementation">The implementation of <typeparamref name="TService"/>.</typeparam>
        /// <param name="services">The service collection.</param>
        public static void AddFromExisting<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService
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
    }
}
