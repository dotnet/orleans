﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost.Extensions;
using Orleans.TestingHost.Utils;
using Orleans.Hosting;

namespace Orleans.TestingHost
{
    /// <summary>Configuration builder for starting a <see cref="TestCluster"/>. It is not required to use this, but it is way simpler than crafting the configuration manually.</summary>
    [Serializable]
    public class TestClusterOptions
    {
        /// <summary>Extended options to be used as fallbacks in the case that explicit options are not provided by the user.</summary>
        public class FallbackOptions
        {

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
            var basePorts = GetAvailableConsecutiveServerPortsPair();
            this.BaseSiloPort = basePorts.Item1;
            this.BaseGatewayPort = basePorts.Item2;
            this.ExtendedFallbackOptions = extendedFallbackOptions;
            this.SiloBuilderFactoryType = typeof(DefaultSiloBuilderFactory);
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

        internal Type SiloBuilderFactoryType { get; private set; }

        public void UseSiloBuilderFactory<TSiloBuilderFactory>() where TSiloBuilderFactory : ISiloBuilderFactory, new()
        {
            this.SiloBuilderFactoryType = typeof(TSiloBuilderFactory);
        }

        /// <summary>
        /// Default client builder factory
        /// </summary>
        public static Func<ClientConfiguration, IClientBuilder> DefaultClientBuilderFactory = config => new ClientBuilder()
            .UseConfiguration(config)
			.AddApplicationPartsFromAppDomain()
            .AddApplicationPartsFromBasePath()
            .ConfigureLogging(builder => TestingUtils.ConfigureDefaultLoggingBuilder(builder, config.TraceFileName));

        /// <summary>
        /// Factory delegate to create a client builder which will be used to build the <see cref="TestCluster"/> client. 
        /// </summary>
        public Func<ClientConfiguration, IClientBuilder> ClientBuilderFactory { get; set; } = DefaultClientBuilderFactory;

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
            config.DeploymentId = clusterConfig.Globals.DeploymentId;
            config.PropagateActivityId = clusterConfig.Defaults.PropagateActivityId;
            // If a debugger is attached, override the timeout setting
            config.ResponseTimeout = Debugger.IsAttached
                ? TimeSpan.FromMilliseconds(1000000)
                : clusterConfig.Globals.ResponseTimeout;

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


        private static Tuple<int, int> GetAvailableConsecutiveServerPortsPair()
        {
            // Evaluate current system tcp connections
            IPGlobalProperties ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
            IPEndPoint[] tcpConnInfoArray = ipGlobalProperties.GetActiveTcpListeners();

            // each returned port in the pair will have to have at least this amount of available ports following it
            const int consecutivePortsToCheck = 5;

            return Tuple.Create(
                GetAvailableConsecutiveServerPorts(tcpConnInfoArray, 22300, 30000, consecutivePortsToCheck),
                GetAvailableConsecutiveServerPorts(tcpConnInfoArray, 40000, 50000, consecutivePortsToCheck));
        }

        private static int GetAvailableConsecutiveServerPorts(IPEndPoint[] tcpConnInfoArray, int portStartRange, int portEndRange, int consecutivePortsToCheck)
        {
            const int MaxAttempts = 10;

            for (int attempts = 0; attempts < MaxAttempts; attempts++)
            {
                int basePort = ThreadSafeRandom.Next(portStartRange, portEndRange);

                // get ports in buckets, so we don't interfere with parallel runs of this same function
                basePort = basePort - (basePort % consecutivePortsToCheck);
                int endPort = basePort + consecutivePortsToCheck;
                
                // make sure non of the ports in the sub range are in use
                if (tcpConnInfoArray.All(endpoint => endpoint.Port < basePort || endpoint.Port >= endPort))
                    return basePort;
            }

            throw new InvalidOperationException("Cannot find enough free ports to spin up a cluster");
        }
    }
}
