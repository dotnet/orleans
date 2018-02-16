using System;
using System.Collections.Generic;
using Orleans.Providers.Streams.SimpleMessageStream;
using Orleans.Streams;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Extension methods for configuration classes specific to Orleans.dll 
    /// </summary>
    public static class ConfigurationExtensions
    {
        /// <summary>
        /// Configures all cluster nodes to use the specified startup class for dependency injection.
        /// </summary>
        /// <typeparam name="TStartup">Startup type</typeparam>
        [Obsolete("Using ISiloHostBuilder.ConfigureServices(Action configureServices) or ISiloHostBuilder.UseServiceProviderFactory(IServiceProviderFactory factory)")]
        public static void UseStartupType<TStartup>(this ClusterConfiguration config) 
        {
            var startupName = typeof(TStartup).AssemblyQualifiedName;

            foreach(var nodeConfig in config.Overrides.Values) {
                nodeConfig.StartupTypeName = startupName;
            }

            config.Defaults.StartupTypeName = startupName;
        }
    }
}