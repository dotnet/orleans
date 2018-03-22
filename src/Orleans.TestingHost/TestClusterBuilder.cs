using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Configuration;
using Orleans.TestingHost.Utils;

namespace Orleans.TestingHost
{
    /// <summary>Configuration builder for starting a <see cref="TestCluster"/>.</summary>
    public class TestClusterBuilder
    {
        private readonly List<Action<IConfigurationBuilder>> configureHostConfigActions = new List<Action<IConfigurationBuilder>>();
        private readonly List<Action> configureBuilderActions = new List<Action>();

        /// <summary>
        /// Initializes a new instance of <see cref="TestClusterBuilder"/> using the default options.
        /// </summary>
        public TestClusterBuilder()
            : this(2)
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="TestClusterBuilder"/> overriding the initial silos count.
        /// </summary>
        /// <param name="initialSilosCount">The number of initial silos to deploy.</param>
        public TestClusterBuilder(short initialSilosCount)
        {
            this.Options = new TestClusterOptions
            {
                InitialSilosCount = initialSilosCount,
                ClusterId = CreateClusterId(),
                ServiceId = Guid.NewGuid().ToString("N"),
                UseTestClusterMembership = true,
                InitializeClientOnDeploy = true,
                ConfigureFileLogging = true,
                AssumeHomogenousSilosForTesting = true
            };

            this.ConfigureBuilder(ConfigureDefaultPorts);
        }
        
        public Dictionary<string, object> Properties { get; } = new Dictionary<string, object>();

        public TestClusterOptions Options { get; }

        /// <summary>
        /// Delegate used to create and start an individual silo.
        /// </summary>
        public Func<string, IList<IConfigurationSource>, SiloHandle> CreateSilo { private get; set; }

        public TestClusterBuilder ConfigureBuilder(Action configureDelegate)
        {
            this.configureBuilderActions.Add(configureDelegate ?? throw new ArgumentNullException(nameof(configureDelegate)));
            return this;
        }

        /// <summary>
        /// Set up the configuration for the builder itself. This will be used as a base to initialize each silo host
        /// for use later in the build process. This can be called multiple times and the results will be additive.
        /// </summary>
        /// <param name="configureDelegate">The delegate for configuring the <see cref="IConfigurationBuilder"/> that will be used
        /// to construct the <see cref="IConfiguration"/> for the host.</param>
        /// <returns>The same instance of the host builder for chaining.</returns>
        public TestClusterBuilder ConfigureHostConfiguration(Action<IConfigurationBuilder> configureDelegate)
        {
            this.configureHostConfigActions.Add(configureDelegate ?? throw new ArgumentNullException(nameof(configureDelegate)));
            return this;
        }

        public void AddSiloBuilderConfigurator<TSiloBuilderConfigurator>() where TSiloBuilderConfigurator : ISiloBuilderConfigurator, new()
        {
            this.Options.SiloBuilderConfiguratorTypes.Add(typeof(TSiloBuilderConfigurator).AssemblyQualifiedName);
        }

        public void AddClientBuilderConfigurator<TClientBuilderConfigurator>() where TClientBuilderConfigurator : IClientBuilderConfigurator, new()
        {
            this.Options.ClientBuilderConfiguratorTypes.Add(typeof(TClientBuilderConfigurator).AssemblyQualifiedName);
        }

        public TestCluster Build()
        {
            var configBuilder = new ConfigurationBuilder();

            foreach (var action in configureBuilderActions)
            {
                action();
            }

            configBuilder.AddInMemoryCollection(this.Options.ToDictionary());
            foreach (var buildAction in this.configureHostConfigActions)
            {
                buildAction(configBuilder);
            }

            var configuration = configBuilder.Build();
            var finalOptions = new TestClusterOptions();
            configuration.Bind(finalOptions);
            
            var configSources = new ReadOnlyCollection<IConfigurationSource>(configBuilder.Sources);
            var testCluster = new TestCluster(finalOptions, configSources);
            if (this.CreateSilo != null) testCluster.CreateSilo = this.CreateSilo;
            return testCluster;
        }

        public static string CreateClusterId()
        {
            string prefix = "testcluster-";
            int randomSuffix = ThreadSafeRandom.Next(1000);
            DateTime now = DateTime.UtcNow;
            string DateTimeFormat = @"yyyy-MM-dd\tHH-mm-ss";
            return $"{prefix}{now.ToString(DateTimeFormat, CultureInfo.InvariantCulture)}-{randomSuffix}";
        }

        private void ConfigureDefaultPorts()
        {
            // Set base ports if none are currently set.
            (int baseSiloPort, int baseGatewayPort) = GetAvailableConsecutiveServerPortsPair(this.Options.InitialSilosCount + 3);
            if (this.Options.BaseSiloPort == 0) this.Options.BaseSiloPort = baseSiloPort;
            if (this.Options.BaseGatewayPort == 0) this.Options.BaseGatewayPort = baseGatewayPort;
        }

        // Returns a pairs of ports which have the specified number of consecutive ports available for use.
        internal static ValueTuple<int, int> GetAvailableConsecutiveServerPortsPair(int consecutivePortsToCheck = 5)
        {
            // Evaluate current system tcp connections
            IPGlobalProperties ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
            IPEndPoint[] tcpConnInfoArray = ipGlobalProperties.GetActiveTcpListeners();

            // each returned port in the pair will have to have at least this amount of available ports following it

            return (GetAvailableConsecutiveServerPorts(tcpConnInfoArray, 22300, 30000, consecutivePortsToCheck),
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