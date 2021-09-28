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
        public IDictionary<object, object> Properties => this.hostBuilder.Properties;

        public SiloBuilder(IHostBuilder hostBuilder)
        {
            this.hostBuilder = hostBuilder;
            hostBuilder.ConfigureServices((ctx, serviceCollection) => serviceCollection.AddHostedService<SiloHostedService>());
            this.ConfigureDefaults();
        }

        /// <inheritdoc />
        public ISiloBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            hostBuilder.ConfigureServices(configureDelegate);
            return this;
        }
    }
}