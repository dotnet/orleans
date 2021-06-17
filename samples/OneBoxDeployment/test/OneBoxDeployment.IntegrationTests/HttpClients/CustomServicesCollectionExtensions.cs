using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http;

namespace OneBoxDeployment.IntegrationTests.HttpClients
{
    /// <summary>
    /// Extension methods for <see cref="IServiceCollection"/>.
    /// </summary>
    public static class CustomServicesCollectionExtensions
    {
        /// <summary>
        /// Registers a transient typed HTTP Client.
        /// </summary>
        /// <typeparam name="TClient">The type to register.</typeparam>
        /// <param name="services">The service collection to where to register.</param>
        /// <param name="configureClient">The configuration for the client.</param>
        /// <returns></returns>
        public static IHttpClientBuilder AddTypedHttpClient<TClient>(this IServiceCollection services, Action<HttpClient> configureClient) where TClient: class
        {
            if(services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if(configureClient == null)
            {
                throw new ArgumentNullException(nameof(configureClient));
            }

            //services.Add(new ServiceDescriptor(typeof(TClient), f => f., ServiceLifetime.Transient));
            services.AddTransient<TClient>();
            return services.AddHttpClient(typeof(TClient).AssemblyQualifiedName, configureClient);
        }
    }
}
