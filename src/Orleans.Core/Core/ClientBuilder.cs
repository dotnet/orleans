using Microsoft.Extensions.DependencyInjection;

namespace Orleans
{
    /// <summary>
    /// Builder for configuring an Orleans client.
    /// </summary>
    public class ClientBuilder : IClientBuilder
    {
        public ClientBuilder(IServiceCollection services)
        {
            Services = services;
            DefaultClientServices.AddDefaultServices(services);
        }

        /// <inheritdoc/>
        public IServiceCollection Services { get; }
    }
}