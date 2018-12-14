using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orleans.Hosting
{
    /// <summary>
    /// Internal wrapper type of <see cref="IHostBuilder"/> that scope all configuration extensions related to orleans.
    /// </summary>
    internal class SiloBuilder : ISiloBuilder
    {
        private readonly IHostBuilder hostBuilder;

        /// <inheritdoc />
        public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

        public SiloBuilder(IHostBuilder hostBuilder)
        {
            this.hostBuilder = hostBuilder;
            this.ConfigureDefaults();

            this.hostBuilder.ConfigureServices((context, services) =>
            {
                services.AddHostedService<SiloHostedService>();
            });

            this.ConfigureApplicationParts(parts =>
            {
                // If the user has not added any application parts, add some defaults.
                parts.ConfigureDefaults();
            });
        }

        /// <inheritdoc />
        public ISiloBuilder ConfigureServices(Action<Microsoft.Extensions.Hosting.HostBuilderContext, IServiceCollection> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            this.hostBuilder.ConfigureServices(configureDelegate);
            return this;
        }
    }
}