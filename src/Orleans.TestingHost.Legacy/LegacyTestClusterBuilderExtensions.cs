using System;
using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Extensions for configuring a <see cref="TestClusterBuilder"/> with legacy configuration objects.
    /// </summary>
    public static class LegacyTestClusterBuilderExtensions
    {
        /// <summary>
        /// Configures a <see cref="TestClusterBuilder"/> with legacy configuration objects,
        /// <see cref="Orleans.Runtime.Configuration.ClusterConfiguration"/> and <see cref="Orleans.Runtime.Configuration.ClientConfiguration"/>.
        /// </summary>
        /// <param name="builder">The test cluster builder.</param>
        /// <param name="configure">The configuration delegate.</param>
        public static void ConfigureLegacyConfiguration(this TestClusterBuilder builder, Action<LegacyTestClusterConfiguration> configure = null)
        {
            if (!builder.Properties.TryGetValue(nameof(LegacyTestClusterConfiguration), out var legacyConfigObj) ||
                !(legacyConfigObj is LegacyTestClusterConfiguration legacyConfig))
            {
                legacyConfig = new LegacyTestClusterConfiguration(builder);
                builder.Properties[nameof(LegacyTestClusterConfiguration)] = legacyConfig;

                builder.ConfigureHostConfiguration(legacyConfig.ConfigureHostConfiguration);
                builder.AddSiloBuilderConfigurator<AddLegacyConfigurationSiloConfigurator>();
                builder.AddClientBuilderConfigurator<AddLegacyConfiguratorClientConfigurator>();
            }

            builder.ConfigureBuilder(() =>
            {
                configure?.Invoke(legacyConfig);
            });
        }

        internal class AddLegacyConfigurationSiloConfigurator : LegacySiloBuilderConfigurator
        {
            public override void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.AddLegacyClusterConfigurationSupport(this.ClusterConfiguration);
            }
        }

        internal class AddLegacyConfiguratorClientConfigurator : LegacyClientBuilderConfigurator
        {
            public override void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.ConfigureServices(services => { services.AddLegacyClientConfigurationSupport(this.ClientConfiguration); });
            }
        }
    }
}