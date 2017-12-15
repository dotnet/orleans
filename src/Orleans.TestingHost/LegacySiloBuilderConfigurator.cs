using System;
using Orleans.Hosting;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Base class for <see cref="ISiloBuilderConfigurator"/> implementations which use legacy configuration objects.
    /// </summary>
    public abstract class LegacySiloBuilderConfigurator : ISiloBuilderConfigurator
    {
        /// <summary>
        /// The test cluster options.
        /// </summary>
        public TestClusterOptions TestClusterOptions { get; private set; }

        /// <summary>
        /// The cluster configuration.
        /// </summary>
        public ClusterConfiguration ClusterConfiguration { get; private set; }

        /// <inheritdoc />
        void ISiloBuilderConfigurator.Configure(ISiloHostBuilder hostBuilder)
        {
            this.TestClusterOptions = hostBuilder.GetTestClusterOptions();

            if (!hostBuilder.Properties.TryGetValue(nameof(ClusterConfiguration), out var configObj))
            {
                var serializationManager = LegacyTestClusterConfiguration.CreateLegacyConfigurationSerializer();

                var serializedClusterConfig = hostBuilder.GetConfigurationValue(LegacyTestClusterConfiguration.ClusterConfigurationKey);
                if (!string.IsNullOrWhiteSpace(serializedClusterConfig))
                {
                    this.ClusterConfiguration = LegacyTestClusterConfiguration.Deserialize<ClusterConfiguration>(serializationManager, serializedClusterConfig);
                    hostBuilder.Properties[nameof(ClusterConfiguration)] = this.ClusterConfiguration;
                }
            }
            else
            {
                this.ClusterConfiguration = (ClusterConfiguration) configObj;
            }

            if (this.ClusterConfiguration == null)
            {
                throw new InvalidOperationException("There is no ClusterConfiguration, which is unexpected for the current set up.");
            }

            this.Configure(hostBuilder);
        }

        /// <summary>
        /// Configures the host builder.
        /// </summary>
        public abstract void Configure(ISiloHostBuilder hostBuilder);
    }
}