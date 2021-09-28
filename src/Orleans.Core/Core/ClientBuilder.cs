using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Orleans
{
    /// <summary>
    /// Builder used for creating <see cref="IClusterClient"/> instances.
    /// </summary>
    public class ClientBuilder : IClientBuilder
    {
        private readonly IHostBuilder hostBuilder;

        public ClientBuilder(IHostBuilder hostBuilder)
        {
            this.hostBuilder = hostBuilder;
            this.ConfigureDefaults();
        }
        
        /// <inheritdoc />
        public IDictionary<object, object> Properties => this.hostBuilder.Properties;

        /// <inheritdoc />
        public IClientBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            this.hostBuilder.ConfigureServices(configureDelegate);
            return this;
        }
    }
}