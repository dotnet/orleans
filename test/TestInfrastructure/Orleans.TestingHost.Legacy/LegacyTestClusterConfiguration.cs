using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.ApplicationParts;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Serialization;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Represents legacy configuration used for configuring a <see cref="TestClusterBuilder"/>.
    /// </summary>
    public class LegacyTestClusterConfiguration
    {
        internal const string ClusterConfigurationKey = nameof(ClusterConfiguration);
        internal const string ClientConfigurationKey = nameof(ClientConfiguration);
        private readonly TestClusterBuilder builder;

        public LegacyTestClusterConfiguration(TestClusterBuilder builder)
        {
            this.builder = builder;
        }
        
        /// <summary>Gets or sets the cluster configuration.</summary>
        public ClusterConfiguration ClusterConfiguration
        {
            get => (ClusterConfiguration)(this.builder.Properties.TryGetValue(ClusterConfigurationKey, out var config)
                ? config
                : (this.builder.Properties[ClusterConfigurationKey] = BuildClusterConfiguration()));
            set => this.builder.Properties[ClusterConfigurationKey] = value;
        }

        /// <summary>Gets or sets the client configuration.</summary>
        public ClientConfiguration ClientConfiguration
        {
            get => (ClientConfiguration)(this.builder.Properties.TryGetValue(ClientConfigurationKey, out var config)
                ? config
                : (this.builder.Properties[ClientConfigurationKey] = BuildClientConfiguration(this.ClusterConfiguration)));
            set => this.builder.Properties[ClientConfigurationKey] = value;
        }
        
        /// <summary>Build a cluster configuration.</summary>
        /// <returns>The builded cluster configuration</returns>
        public ClusterConfiguration BuildClusterConfiguration()
        {
            var config = ClusterConfiguration.LocalhostPrimarySilo(builder.Options.BaseSiloPort, builder.Options.BaseGatewayPort);
            if (string.IsNullOrWhiteSpace(config.Globals.ClusterId))
            {
                config.Globals.ClusterId = this.builder.Options.ClusterId;
            }
            config.Globals.ExpectedClusterSize = this.builder.Options.InitialSilosCount;
            config.Globals.AssumeHomogenousSilosForTesting = this.builder.Options.AssumeHomogenousSilosForTesting;

            // If a debugger is attached, override the timeout setting
            if (Debugger.IsAttached)
            {
                config.Globals.ResponseTimeout = TimeSpan.FromMilliseconds(1000000);
            }

            for (var i = 0; i < builder.Options.InitialSilosCount; i++)
            {
                var siloConfig = TestSiloSpecificOptions.Create(this.builder.Options, i);
                var nodeConfig = config.GetOrCreateNodeConfigurationForSilo(siloConfig.SiloName);
                nodeConfig.Port = siloConfig.SiloPort;
                nodeConfig.SiloName = siloConfig.SiloName;
                var address = ConfigUtilities.ResolveIPAddress(nodeConfig.HostNameOrIPAddress, nodeConfig.Subnet, nodeConfig.AddressType).GetAwaiter().GetResult();
                nodeConfig.ProxyGatewayEndpoint = siloConfig.GatewayPort != 0 ? new IPEndPoint(address, siloConfig.GatewayPort) : null;
                nodeConfig.IsPrimaryNode = i == 0;
            }

            return config;
        }

        /// <summary>
        /// Build the client configuration based on the cluster configuration.
        /// </summary>
        /// <param name="clusterConfig">The reference cluster configuration.</param>
        /// <returns>The client configuration</returns>
        public ClientConfiguration BuildClientConfiguration(ClusterConfiguration clusterConfig)
        {
            var config = new ClientConfiguration();
            switch (clusterConfig.Globals.LivenessType)
            {
                case GlobalConfiguration.LivenessProviderType.AzureTable:
                    config.GatewayProvider = ClientConfiguration.GatewayProviderType.AzureTable;
                    break;
                case GlobalConfiguration.LivenessProviderType.AdoNet:
                    config.GatewayProvider = ClientConfiguration.GatewayProviderType.AdoNet;
                    break;
                case GlobalConfiguration.LivenessProviderType.ZooKeeper:
                    config.GatewayProvider = ClientConfiguration.GatewayProviderType.ZooKeeper;
                    break;
                case GlobalConfiguration.LivenessProviderType.Custom:
                    config.GatewayProvider = ClientConfiguration.GatewayProviderType.Custom;
                    config.CustomGatewayProviderAssemblyName = clusterConfig.Globals.MembershipTableAssembly;
                    break;
                case GlobalConfiguration.LivenessProviderType.NotSpecified:
                case GlobalConfiguration.LivenessProviderType.MembershipTableGrain:
                default:
                    config.GatewayProvider = ClientConfiguration.GatewayProviderType.Config;
                    var primaryProxyEndpoint = clusterConfig.GetOrCreateNodeConfigurationForSilo(Silo.PrimarySiloName).ProxyGatewayEndpoint;
                    if (primaryProxyEndpoint != null)
                    {
                        config.Gateways.Add(primaryProxyEndpoint);
                    }
                    foreach (var nodeConfiguration in clusterConfig.Overrides.Values.Where(x => x.SiloName != Silo.PrimarySiloName && x.ProxyGatewayEndpoint != null))
                    {
                        config.Gateways.Add(nodeConfiguration.ProxyGatewayEndpoint);
                    }
                    break;
            }

            config.DataConnectionString = clusterConfig.Globals.DataConnectionString;
            config.AdoInvariant = clusterConfig.Globals.AdoInvariant;
            config.ClusterId = clusterConfig.Globals.ClusterId;
            config.PropagateActivityId = clusterConfig.Defaults.PropagateActivityId;
            // If a debugger is attached, override the timeout setting
            config.ResponseTimeout = Debugger.IsAttached
                ? TimeSpan.FromMilliseconds(1000000)
                : clusterConfig.Globals.ResponseTimeout;

            return config;
        }

        internal void ConfigureHostConfiguration(IConfigurationBuilder configBuilder)
        {
            // Serialize the configuration so that the client/silo can read it during startup.
            SerializationManager serializationManager = CreateLegacyConfigurationSerializer();
            configBuilder.AddInMemoryCollection(new Dictionary<string, string>
            {
                [ClusterConfigurationKey] = Serialize(serializationManager, this.ClusterConfiguration),
                [ClientConfigurationKey] = Serialize(serializationManager, this.ClientConfiguration)
            });
        }

        internal static SerializationManager CreateLegacyConfigurationSerializer()
        {
            var applicationPartManager = new ApplicationPartManager();
            applicationPartManager.AddFeatureProvider(new BuiltInTypesSerializationFeaturePopulator());
            applicationPartManager.AddFeatureProvider(new AssemblyAttributeFeatureProvider<SerializerFeature>());
            applicationPartManager.AddApplicationPart(typeof(ClusterConfiguration).Assembly).WithReferences();
            var serializationManager = new SerializationManager(null,
                Options.Create(new SerializationProviderOptions()),
                new NullLoggerFactory(),
                new CachedTypeResolver(),
                new SerializationStatisticsGroup(Options.Create(new StatisticsOptions {CollectionLevel = StatisticsLevel.Info})));
            serializationManager.RegisterSerializers(applicationPartManager);
            return serializationManager;
        }

        internal static string Serialize(SerializationManager serializationManager, object config)
        {
            BufferPool.InitGlobalBufferPool(new SiloMessagingOptions());
            var writer = new BinaryTokenStreamWriter();
            serializationManager.Serialize(config, writer);
            string serialized = Convert.ToBase64String(writer.ToByteArray());
            writer.ReleaseBuffers();
            return serialized;
        }

        internal static T Deserialize<T>(SerializationManager serializationManager, string config)
        {
            var data = Convert.FromBase64String(config);
            var reader = new BinaryTokenStreamReader(data);
            return serializationManager.Deserialize<T>(reader);
        }
    }
}