using System;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;

namespace Orleans.TestingHost
{
    public static class TestClusterExtensions
    {
        public static IConfiguration GetConfiguration(this ISiloHostBuilder hostBuilder)
        {
            if (hostBuilder.Properties.TryGetValue("Configuration", out var configObject) && configObject is IConfiguration config)
            {
                return config;
            }

            throw new InvalidOperationException(
                $"Expected configuration object in \"Configuration\" property of type {nameof(IConfiguration)} on {nameof(ISiloHostBuilder)}.");
        }

        public static string GetConfigurationValue(this ISiloHostBuilder hostBuilder, string key)
        {
            return hostBuilder.GetConfiguration()[key];
        }

        public static TestClusterOptions GetTestClusterOptions(this ISiloHostBuilder hostBuilder)
        {
            return hostBuilder.GetConfiguration().GetTestClusterOptions();
        }

        public static TestClusterOptions GetTestClusterOptions(this IConfiguration config)
        {
            var result = new TestClusterOptions();
            config.Bind(result);
            return result;
        }
    }
}
