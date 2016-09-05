﻿using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost.Extensions;
using Orleans.TestingHost.Utils;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Configuration builder for starting a <see cref="TestCluster"/>. It is not required to use this, but it is way simpler than crafting the configuration manually.
    /// </summary>
    [Serializable]
    public class TestClusterOptions
    {
        private ClusterConfiguration _clusterConfiguration;
        private ClientConfiguration _clientConfiguration;

        public TestClusterOptions()
            : this(DefaultInitialSilosCount)
        {
        }

        public TestClusterOptions(short initialSilosCount)
        {
            this.InitialSilosCount = initialSilosCount;
            this.BaseSiloPort = ThreadSafeRandom.Next(22300, 30000);
            this.BaseGatewayPort = ThreadSafeRandom.Next(40000, 50000);
        }

        public static short DefaultInitialSilosCount { get; set; } = 2;
        public static bool DefaultTraceToConsole { get; set; } = true;
        public static string DefaultLogsFolder { get; set; } = "logs";

        public int BaseGatewayPort { get; set; }

        public int BaseSiloPort { get; set; }

        public short InitialSilosCount { get; set; }

        public ClusterConfiguration ClusterConfiguration
        {
            get { return _clusterConfiguration ??
                    (_clusterConfiguration = BuildClusterConfiguration()); }
            set { _clusterConfiguration = value; }
        }

        public ClientConfiguration ClientConfiguration
        {
            get { return _clientConfiguration ??
                    (_clientConfiguration = BuildClientConfiguration(this.ClusterConfiguration)); }
            set { _clientConfiguration = value; }
        }

        private ClusterConfiguration BuildClusterConfiguration()
        {
            if (this.InitialSilosCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(InitialSilosCount), this.InitialSilosCount, "value must be greater than 0.");
            }

            int silosCount = this.InitialSilosCount;
            int baseSiloPort = this.BaseSiloPort;
            int baseGatewayPort = this.BaseGatewayPort;

            return BuildClusterConfiguration(baseSiloPort, baseGatewayPort, silosCount);
        }

        public ClusterConfiguration BuildClusterConfiguration(int baseSiloPort, int baseGatewayPort, int silosCount)
        {
            var config = ClusterConfiguration.LocalhostPrimarySilo(baseSiloPort, baseGatewayPort);
            config.Globals.DeploymentId = CreateDeploymentId();
            config.Defaults.TraceToConsole = DefaultTraceToConsole;
            if (!string.IsNullOrWhiteSpace(DefaultLogsFolder))
            {
                if (!Directory.Exists(DefaultLogsFolder))
                {
                    Directory.CreateDirectory(DefaultLogsFolder);
                }

                config.Defaults.TraceFilePattern = $"{DefaultLogsFolder}\\{config.Defaults.TraceFilePattern}";
            }

            AddNodeConfiguration(config, Silo.SiloType.Primary, 0, baseSiloPort, baseGatewayPort);
            for (short instanceNumber = 1; instanceNumber < silosCount; instanceNumber++)
            {
                AddNodeConfiguration(config, Silo.SiloType.Secondary, instanceNumber, baseSiloPort, baseGatewayPort);
            }

            config.Globals.ExpectedClusterSize = silosCount;

            config.AdjustForTestEnvironment();
            return config;
        }

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

        public static ClientConfiguration BuildClientConfiguration(ClusterConfiguration clusterConfig)
        {
            var config = new ClientConfiguration();
            config.TraceFilePattern = clusterConfig.Defaults.TraceFilePattern;
            config.TraceToConsole = DefaultTraceToConsole;
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
            if (Debugger.IsAttached)
            {
                // Test is running inside debugger - Make timeout ~= infinite
                config.ResponseTimeout = TimeSpan.FromMilliseconds(1000000);
            }
            else
            {
                config.ResponseTimeout = clusterConfig.Globals.ResponseTimeout;
            }

            config.LargeMessageWarningThreshold = clusterConfig.Defaults.LargeMessageWarningThreshold;

            // TODO: copy test environment config from globals instead of from constants
            config.AdjustForTestEnvironment();
            return config;
        }

        private string CreateDeploymentId()
        {
            string prefix = "testdepid-";
            int randomSuffix = ThreadSafeRandom.Next(1000);
            DateTime now = DateTime.UtcNow;
            string DateTimeFormat = @"yyyy-MM-dd\tHH-mm-ss";
            string depId = $"{prefix}{now.ToString(DateTimeFormat, CultureInfo.InvariantCulture)}-{this.BaseSiloPort}-{randomSuffix}";
            return depId;
        }
    }
}
