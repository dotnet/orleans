using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost.Extensions;
using Orleans.TestingHost.Utils;

namespace Orleans.TestingHost
{
    /// <summary>Configuration builder for starting a <see cref="TestCluster"/>. It is not required to use this, but it is way simpler than crafting the configuration manually.</summary>
    [Serializable]
    public class TestClusterOptions
    {
        /// <summary>Extended options to be used as fallbacks in the case that explicit options are not provided by the user.</summary>
        public class FallbackOptions
        {
            /// <summary>Gets or sets whether the cluster will output traces in the console by default</summary>
            public bool? TraceToConsole { get; set; }

            /// <summary>Gets or sets the default subfolder the the logs</summary>
            public string LogsFolder { get; set; }

            /// <summary>Gets or sets the default data connection string to use in tests</summary>
            public string DataConnectionString { get; set; }

            /// <summary>Gets or sets the default initial silo count</summary>
            public short InitialSilosCount { get; set; }

            /// <summary>Creates a default configuration builder with some defaults.</summary>
            public static ConfigurationBuilder DefaultConfigurationBuilder()
            {
                var builder = new ConfigurationBuilder();
                builder.AddInMemoryCollection(new Dictionary<string, string>
                {
                    { nameof(DataConnectionString), "UseDevelopmentStorage=true" },
                    { nameof(LogsFolder), "logs" },
                    { nameof(TraceToConsole), "true" },
                    { nameof(InitialSilosCount), "2" },
                });
                return builder;
            }

            /// <summary>Configure defaults for building the configurations</summary>
            public static IConfiguration DefaultExtendedConfiguration { get; set; } = DefaultConfigurationBuilder().Build();
        }

        private ClusterConfiguration _clusterConfiguration;
        private ClientConfiguration _clientConfiguration;

        /// <summary>
        /// Initializes a new instance of <see cref="TestClusterOptions"/> using the default <see cref="ExtendedFallbackOptions"/> specified by <see cref="FallbackOptions.DefaultExtendedConfiguration"/>.
        /// </summary>
        public TestClusterOptions()
            : this(FallbackOptions.DefaultExtendedConfiguration)
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="TestClusterOptions"/> overriding the initial silos count and using the default <see cref="ExtendedFallbackOptions"/> specified by <see cref="FallbackOptions.DefaultExtendedConfiguration"/>.
        /// </summary>
        /// <param name="initialSilosCount">The number of initial silos to deploy.</param>
        public TestClusterOptions(short initialSilosCount)
            : this(FallbackOptions.DefaultExtendedConfiguration)
        {
            this.ExtendedFallbackOptions.InitialSilosCount = initialSilosCount;
        }


        /// <summary>
        /// Initializes a new instance of <see cref="TestClusterOptions"/> using the specified configuration.
        /// </summary>
        /// <param name="extendedConfiguration">Configuration that can be bound to an instance of <see cref="ExtendedFallbackOptions"/>.</param>
        public TestClusterOptions(IConfiguration extendedConfiguration)
            : this(BindExtendedOptions(extendedConfiguration))
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="TestClusterOptions"/>.
        /// </summary>
        /// <param name="extendedFallbackOptions">Fallback options to use when they are not explicitly specified in the <see cref="ClusterConfiguration"/>.</param>
        public TestClusterOptions(FallbackOptions extendedFallbackOptions)
        {
            this.BaseSiloPort = ThreadSafeRandom.Next(22300, 30000);
            this.BaseGatewayPort = ThreadSafeRandom.Next(40000, 50000);
            this.ExtendedFallbackOptions = extendedFallbackOptions;
        }

        private static FallbackOptions BindExtendedOptions(IConfiguration extendedConfiguration)
        {
            var fallbackOptions = new FallbackOptions();
            extendedConfiguration.Bind(fallbackOptions);
            return fallbackOptions;
        }

        /// <summary>
        /// Gets or sets fallback options in the case that some configuration settings are not explicitly provided by the user, such as the <see cref="GlobalConfiguration.DataConnectionString"/>
        /// </summary>
        public FallbackOptions ExtendedFallbackOptions { get; set; }

        /// <summary>Gets or sets the base port number to use for silo's gateways</summary>
        public int BaseGatewayPort { get; set; }

        /// <summary>Gets or sets the base port number to use for silos
        /// </summary>
        public int BaseSiloPort { get; set; }

        /// <summary>Gets or sets the cluster configuration. If no value is specified when getting the configuration, a new one will be built with <see cref="BuildClusterConfiguration()"/></summary>
        public ClusterConfiguration ClusterConfiguration
        {
            get { return _clusterConfiguration ??
                    (_clusterConfiguration = BuildClusterConfiguration()); }
            set { _clusterConfiguration = value; }
        }

        /// <summary>Gets or sets the client configuration. If no value is specified when getting the configuration, a new one will be built with <see cref="BuildClientConfiguration(Runtime.Configuration.ClusterConfiguration)"/></summary>
        public ClientConfiguration ClientConfiguration
        {
            get { return _clientConfiguration ??
                    (_clientConfiguration = BuildClientConfiguration(this.ClusterConfiguration)); }
            set { _clientConfiguration = value; }
        }

        private ClusterConfiguration BuildClusterConfiguration()
        {
            int silosCount = this.ExtendedFallbackOptions.InitialSilosCount;
            if (silosCount < 1)
            {
                throw new InvalidOperationException($"{nameof(ExtendedFallbackOptions)}.{nameof(FallbackOptions.InitialSilosCount)} must be greater than 0. Current value is {silosCount}");
            }

            int baseSiloPort = this.BaseSiloPort;
            int baseGatewayPort = this.BaseGatewayPort;

            return BuildClusterConfiguration(baseSiloPort, baseGatewayPort, silosCount, this.ExtendedFallbackOptions);
        }

        /// <summary>Build a cluster configuration.</summary>
        /// <param name="baseSiloPort">Base port number to use for silos</param>
        /// <param name="baseGatewayPort">Base port number to use for silo's gateways</param>
        /// <param name="silosCount">The number of initial silos to deploy.</param>
        /// <param name="extendedOptions">The extended fallback options.</param>
        /// <returns>The builded cluster configuration</returns>
        public static ClusterConfiguration BuildClusterConfiguration(int baseSiloPort, int baseGatewayPort, int silosCount, FallbackOptions extendedOptions)
        {
            var config = ClusterConfiguration.LocalhostPrimarySilo(baseSiloPort, baseGatewayPort);
            config.Globals.DeploymentId = CreateDeploymentId(baseSiloPort);
            if (extendedOptions.TraceToConsole.HasValue)
            {
                config.Defaults.TraceToConsole = extendedOptions.TraceToConsole.Value;
            }

            var defaultLogsFolder = extendedOptions.LogsFolder;
            if (!string.IsNullOrWhiteSpace(defaultLogsFolder))
            {
                if (!Directory.Exists(defaultLogsFolder))
                {
                    Directory.CreateDirectory(defaultLogsFolder);
                }

                config.Defaults.TraceFilePattern = $"{defaultLogsFolder}\\{config.Defaults.TraceFilePattern}";
            }

            AddNodeConfiguration(config, Silo.SiloType.Primary, 0, baseSiloPort, baseGatewayPort);
            for (short instanceNumber = 1; instanceNumber < silosCount; instanceNumber++)
            {
                AddNodeConfiguration(config, Silo.SiloType.Secondary, instanceNumber, baseSiloPort, baseGatewayPort);
            }

            config.Globals.ExpectedClusterSize = silosCount;
            config.Globals.AssumeHomogenousSilosForTesting = true;

            config.AdjustForTestEnvironment(extendedOptions.DataConnectionString);
            return config;
        }

        /// <summary>Adds a silo config to the target cluster config.</summary>
        /// <param name="config">The target cluster configuration</param>
        /// <param name="siloType">The type of the silo to add</param>
        /// <param name="instanceNumber">The instance number of the silo</param>
        /// <param name="baseSiloPort">Base silo port to use</param>
        /// <param name="baseGatewayPort">Base gateway silo port to use</param>
        /// <returns>The silo configuration added</returns>
        public static NodeConfiguration AddNodeConfiguration(ClusterConfiguration config, Silo.SiloType siloType, short instanceNumber, int baseSiloPort, int baseGatewayPort)
        {
            string siloName;
            switch (siloType)
            {
                case Silo.SiloType.Primary:
                    siloName = Silo.PrimarySiloName;
                    break;
                default:
                    siloName = $"Secondary_{instanceNumber}";
                    break;
            }

            NodeConfiguration nodeConfig = config.GetOrCreateNodeConfigurationForSilo(siloName);
            nodeConfig.HostNameOrIPAddress = "loopback";
            nodeConfig.Port = baseSiloPort + instanceNumber;
            var proxyAddress = nodeConfig.ProxyGatewayEndpoint?.Address ?? config.Defaults.ProxyGatewayEndpoint?.Address;
            if (proxyAddress != null)
            {
                nodeConfig.ProxyGatewayEndpoint = new IPEndPoint(proxyAddress, baseGatewayPort + instanceNumber);
            }

            config.Overrides[siloName] = nodeConfig;
            return nodeConfig;
        }

        /// <summary>
        /// Build the client configuration based on the cluster configuration. If a debugger is attached, 
        /// the response timeout will be overridden to 1000000ms
        /// </summary>
        /// <param name="clusterConfig">The reference cluster configuration.</param>
        /// <returns>THe builded client configuration</returns>
        public static ClientConfiguration BuildClientConfiguration(ClusterConfiguration clusterConfig)
        {
            var config = new ClientConfiguration();
            config.TraceFilePattern = clusterConfig.Defaults.TraceFilePattern;
            config.TraceToConsole = clusterConfig.Defaults.TraceToConsole;
            switch (clusterConfig.Globals.LivenessType)
            {
                case GlobalConfiguration.LivenessProviderType.AzureTable:
                    config.GatewayProvider = ClientConfiguration.GatewayProviderType.AzureTable;
                    break;
                case GlobalConfiguration.LivenessProviderType.SqlServer:
                    config.GatewayProvider = ClientConfiguration.GatewayProviderType.SqlServer;
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
                    var primaryProxyEndpoint = clusterConfig.Overrides[Silo.PrimarySiloName].ProxyGatewayEndpoint;
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
            config.PropagateActivityId = clusterConfig.Defaults.PropagateActivityId;
            config.DeploymentId = clusterConfig.Globals.DeploymentId;

            // If a debugger is attached, override the timeout setting
            config.ResponseTimeout = Debugger.IsAttached
                ? TimeSpan.FromMilliseconds(1000000)
                : clusterConfig.Globals.ResponseTimeout;

            config.LargeMessageWarningThreshold = clusterConfig.Defaults.LargeMessageWarningThreshold;

            // TODO: copy test environment config from globals instead of from constants
            config.AdjustForTestEnvironment(clusterConfig.Globals.DataConnectionString);
            return config;
        }

        private static string CreateDeploymentId(int baseSiloPort)
        {
            string prefix = "testdepid-";
            int randomSuffix = ThreadSafeRandom.Next(1000);
            DateTime now = DateTime.UtcNow;
            string DateTimeFormat = @"yyyy-MM-dd\tHH-mm-ss";
            string depId = $"{prefix}{now.ToString(DateTimeFormat, CultureInfo.InvariantCulture)}-{baseSiloPort}-{randomSuffix}";
            return depId;
        }
    }
}
