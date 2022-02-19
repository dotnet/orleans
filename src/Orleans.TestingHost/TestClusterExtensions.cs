using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Extension methods for test clusters.
    /// </summary>
    public static class TestClusterExtensions
    {        
        /// <summary>
        /// Gets the configuration from the specified host builder.
        /// </summary>        
        /// <param name="builder">
        /// The builder.
        /// </param>
        public static IConfiguration GetConfiguration(this IHostBuilder builder)
        {
            if (builder.Properties.TryGetValue("Configuration", out var configObject) && configObject is IConfiguration config)
            {
                return config;
            }

            throw new InvalidOperationException(
                $"Expected configuration object in \"Configuration\" property of type {nameof(IConfiguration)} on {nameof(ISiloBuilder)}.");
        }

        /// <summary>
        /// Gets a configuration value.
        /// </summary>
        /// <param name="hostBuilder">The host builder.</param>
        /// <param name="key">The key.</param>
        /// <returns>The configuration value.</returns>
        public static string GetConfigurationValue(this IHostBuilder hostBuilder, string key)
        {
            return hostBuilder.GetConfiguration()[key];
        }

        /// <summary>
        /// Gets the test cluster options.
        /// </summary>
        /// <param name="hostBuilder">The host builder.</param>
        /// <returns>The test cluster options.</returns>
        public static TestClusterOptions GetTestClusterOptions(this IHostBuilder hostBuilder)
        {
            return hostBuilder.GetConfiguration().GetTestClusterOptions();
        }

        /// <summary>
        /// Gets the test cluster options.
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <returns>The test cluster options.</returns>
        public static TestClusterOptions GetTestClusterOptions(this IConfiguration config)
        {
            var result = new TestClusterOptions();
            config.Bind(result);
            return result;
        }
    }
}
