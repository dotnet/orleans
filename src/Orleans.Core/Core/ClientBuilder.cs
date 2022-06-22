using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// Builder for configuring an Orleans client.
    /// </summary>
    public class ClientBuilder : IClientBuilder
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClientBuilder"/> class.
        /// </summary>
        /// <param name="services">
        /// The service collection.
        /// </param>
        public ClientBuilder(IServiceCollection services)
        {
            Services = services;
            DefaultClientServices.AddDefaultServices(services);
        }

        /// <inheritdoc/>
        public IServiceCollection Services { get; }
    }
}