using System;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Hosting
{
    /// <summary>
    /// Anchor type for configuring an Orleans server.
    /// </summary>
    internal class SiloBuilder : ISiloBuilder
    {
        public SiloBuilder(IServiceCollection services)
        {
            DefaultSiloServices.AddDefaultServices(services);
            Services = services;
        }

        public IServiceCollection Services { get; }
    }
}