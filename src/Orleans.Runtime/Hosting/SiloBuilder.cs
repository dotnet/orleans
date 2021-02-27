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
        private readonly List<Action<Microsoft.Extensions.Hosting.HostBuilderContext, ISiloBuilder>> configureSiloDelegates = new List<Action<Microsoft.Extensions.Hosting.HostBuilderContext, ISiloBuilder>>();
        private readonly List<Action<Microsoft.Extensions.Hosting.HostBuilderContext, IServiceCollection>> configureServicesDelegates = new List<Action<Microsoft.Extensions.Hosting.HostBuilderContext, IServiceCollection>>();

        /// <inheritdoc />
        public IDictionary<object, object> Properties => this.hostBuilder.Properties;

        public SiloBuilder(IHostBuilder hostBuilder)
        {
            this.hostBuilder = hostBuilder;
            this.ConfigureDefaults();
        }

        public void Build(Microsoft.Extensions.Hosting.HostBuilderContext context, IServiceCollection serviceCollection)
        {
            foreach (var configurationDelegate in this.configureSiloDelegates)
            {
                configurationDelegate(context, this);
            }

            serviceCollection.AddHostedService<SiloHostedService>();

            foreach (var configurationDelegate in this.configureServicesDelegates)
            {
                configurationDelegate(context, serviceCollection);
            }
        }

        public ISiloBuilder ConfigureSilo(Action<Microsoft.Extensions.Hosting.HostBuilderContext, ISiloBuilder> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            this.configureSiloDelegates.Add(configureDelegate);
            return this;
        }

        /// <inheritdoc />
        public ISiloBuilder ConfigureServices(Action<Microsoft.Extensions.Hosting.HostBuilderContext, IServiceCollection> configureDelegate)
        {
            if (configureDelegate == null) throw new ArgumentNullException(nameof(configureDelegate));
            this.configureServicesDelegates.Add(configureDelegate);
            return this;
        }
    }
}