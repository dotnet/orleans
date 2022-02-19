using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// Builder for configuring an Orleans server.
    /// </summary>
    internal class SiloBuilder : ISiloBuilder
    {
        public SiloBuilder(IServiceCollection services)
        {
            DefaultSiloServices.AddDefaultServices(services);
            Services = services;
        }

        /// <inheritdoc/>
        public IServiceCollection Services { get; }
    }
}