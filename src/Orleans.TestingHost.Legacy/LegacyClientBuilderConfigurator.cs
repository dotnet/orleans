using System;
using Microsoft.Extensions.Configuration;
using Orleans.Runtime.Configuration;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Base class for <see cref="IClientBuilderConfigurator"/> implementations which use legacy configuration objects.
    /// </summary>
    public abstract class LegacyClientBuilderConfigurator : IClientBuilderConfigurator
    {
        /// <summary>
        /// The test cluster options.
        /// </summary>
        public TestClusterOptions TestClusterOptions { get; private set; }

        /// <summary>
        /// The client configuration.
        /// </summary>
        public ClientConfiguration ClientConfiguration { get; private set; }

        /// <summary>
        /// The cluster configuration.
        /// </summary>
        public ClusterConfiguration ClusterConfiguration { get; private set; }

        /// <inheritdoc />
        void IClientBuilderConfigurator.Configure(IConfiguration configuration, IClientBuilder clientBuilder)
        {
            this.TestClusterOptions = configuration.GetTestClusterOptions();
            var serializationManager = LegacyTestClusterConfiguration.CreateLegacyConfigurationSerializer();
            if (!clientBuilder.Properties.TryGetValue(nameof(ClientConfiguration), out var clientConfig))
            {
                var serializedClientConfig = configuration[LegacyTestClusterConfiguration.ClientConfigurationKey];
                if (!string.IsNullOrWhiteSpace(serializedClientConfig))
                {
                    this.ClientConfiguration = LegacyTestClusterConfiguration.Deserialize<ClientConfiguration>(serializationManager, serializedClientConfig);
                    clientBuilder.Properties[nameof(ClientConfiguration)] = this.ClientConfiguration;
                }
            }
            else
            {
                this.ClientConfiguration = (ClientConfiguration) clientConfig;
            }

            if (this.ClientConfiguration == null)
            {
                throw new InvalidOperationException("There is no ClientConfiguration, which is unexpected for the current set up.");
            }

            if (!clientBuilder.Properties.TryGetValue(nameof(ClusterConfiguration), out var clusterConfig))
            {
                var serializedClusterConfig = configuration[LegacyTestClusterConfiguration.ClusterConfigurationKey];
                if (!string.IsNullOrWhiteSpace(serializedClusterConfig))
                {
                    this.ClusterConfiguration = LegacyTestClusterConfiguration.Deserialize<ClusterConfiguration>(serializationManager, serializedClusterConfig);
                    clientBuilder.Properties[nameof(ClusterConfiguration)] = this.ClusterConfiguration;
                }
            }
            else
            {
                this.ClusterConfiguration = (ClusterConfiguration) clusterConfig;
            }
            
            if (this.ClusterConfiguration == null)
            {
                throw new InvalidOperationException("There is no ClusterConfiguration, which is unexpected for the current set up.");
            }

            this.Configure(configuration, clientBuilder);
        }

        /// <summary>
        /// Configures the client builder.
        /// </summary>
        public abstract void Configure(IConfiguration configuration, IClientBuilder clientBuilder);
    }
}