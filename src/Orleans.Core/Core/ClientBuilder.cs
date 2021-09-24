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
        private readonly List<Action<HostBuilderContext, IClientBuilder>> configureClientDelegates = new List<Action<HostBuilderContext, IClientBuilder>>();
        private readonly List<Action<HostBuilderContext, IServiceCollection>> configureServicesDelegates = new List<Action<HostBuilderContext, IServiceCollection>>();

        public ClientBuilder(IHostBuilder hostBuilder)
        {
            this.hostBuilder = hostBuilder;
            this.ConfigureDefaults();
        }
        
        /// <inheritdoc />
        public IDictionary<object, object> Properties => this.hostBuilder.Properties;

        /// <inheritdoc />
        public void Build(HostBuilderContext context, IServiceCollection serviceCollection)
        {
            foreach (var configurationDelegate in this.configureClientDelegates)
            {
                configurationDelegate(context, this);
            }

            foreach (var configurationDelegate in this.configureServicesDelegates)
            {
                configurationDelegate(context, serviceCollection);
            }
        }

        /// <summary>
        /// Registers configuration delegates.
        /// </summary>
        /// <param name="configureDelegate">The delegate.</param>
        /// <returns>The same instance of the host builder for chaining.</returns>
        public IClientBuilder ConfigureClient(Action<HostBuilderContext, IClientBuilder> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            this.configureClientDelegates.Add(configureDelegate);
            return this;
        }

        /// <inheritdoc />
        public IClientBuilder ConfigureServices(Action<HostBuilderContext, IServiceCollection> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            this.configureServicesDelegates.Add(configureDelegate);
            return this;
        }
    }
}