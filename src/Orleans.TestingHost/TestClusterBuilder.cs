using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.TestHooks;

namespace Orleans.TestingHost
{
    /// <summary>Configuration builder for starting a <see cref="TestCluster"/>.</summary>
    public class TestClusterBuilder
    {
        private readonly List<Action<IConfigurationBuilder>> configureHostConfigActions = new List<Action<IConfigurationBuilder>>();
        private readonly List<Action> configureBuilderActions = new List<Action>();
        private Func<string, IConfiguration, Task<SiloHandle>> _createSiloAsync;

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

            this.AddSiloBuilderConfigurator<ConfigureStaticClusterDeploymentOptions>();
            this.ConfigureBuilder(ConfigureDefaultPorts);
        }

        /// <summary>
        /// Gets or sets the port allocator used to allocate consecutive silo ports.
        /// </summary>
        public ITestClusterPortAllocator PortAllocator { get; set; } = new TestClusterPortAllocator();

        /// <summary>
        /// Configuration values which will be provided to the silos and clients created by this builder.
        /// </summary>
        public Dictionary<string, string> Properties { get; } = new Dictionary<string, string>();

        /// <summary>
        /// Gets the options.
        /// </summary>
        /// <value>The options.</value>
        public TestClusterOptions Options { get; }

        /// <summary>
        /// Gets or sets the delegate used to create and start an individual silo.
        /// </summary>
        public Func<string, IConfiguration, Task<SiloHandle>> CreateSiloAsync
        {
            private get => _createSiloAsync;
            set
            {
                _createSiloAsync = value;

                // The custom builder does not have access to the in-memory transport.
                Options.ConnectionTransport = ConnectionTransportType.TcpSocket;
            }
        }
        
        /// <summary>
        /// Adds a configuration delegate to the builder
        /// </summary>        
        /// <param name="configureDelegate">The configuration delegate.</param>
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

        /// <summary>
        /// Adds an implementation of <see cref="ISiloConfigurator"/> or <see cref="IHostConfigurator"/> to configure silos created by the test cluster.
        /// </summary>
        /// <typeparam name="T">The configurator type.</typeparam>
        /// <returns>The builder.</returns>
        public TestClusterBuilder AddSiloBuilderConfigurator<T>() where T : new()
        {
            if (!typeof(ISiloConfigurator).IsAssignableFrom(typeof(T)) && !typeof(IHostConfigurator).IsAssignableFrom(typeof(T)))
            {
                throw new ArgumentException($"The type {typeof(T)} is not assignable to either {nameof(ISiloConfigurator)} or {nameof(IHostConfigurator)}");
            }

            this.Options.SiloBuilderConfiguratorTypes.Add(typeof(T).AssemblyQualifiedName);
            return this;
        }

        /// <summary>
        /// Adds the client builder configurator, which must implement <see cref="IClientBuilderConfigurator"/> or <see cref="IHostConfigurator"/>.
        /// </summary>
        /// <typeparam name="TClientBuilderConfigurator">The client builder type</typeparam>
        /// <returns>The builder.</returns>
        public TestClusterBuilder AddClientBuilderConfigurator<TClientBuilderConfigurator>() where TClientBuilderConfigurator : IClientBuilderConfigurator, new()
        {
            this.Options.ClientBuilderConfiguratorTypes.Add(typeof(TClientBuilderConfigurator).AssemblyQualifiedName);
            return this;
        }

        /// <summary>
        /// Builds this instance.
        /// </summary>
        /// <returns>TestCluster.</returns>
        public TestCluster Build()
        {
            var portAllocator = this.PortAllocator;
            var configBuilder = new ConfigurationBuilder();

            foreach (var action in configureBuilderActions)
            {
                action();
            }

            configBuilder.AddInMemoryCollection(this.Properties);
            configBuilder.AddInMemoryCollection(this.Options.ToDictionary());
            foreach (var buildAction in this.configureHostConfigActions)
            {
                buildAction(configBuilder);
            }

            var configuration = configBuilder.Build();
            var finalOptions = new TestClusterOptions();
            configuration.Bind(finalOptions);
            
            var configSources = new ReadOnlyCollection<IConfigurationSource>(configBuilder.Sources);
            var testCluster = new TestCluster(finalOptions, configSources, portAllocator);
            if (CreateSiloAsync != null) testCluster.CreateSiloAsync = CreateSiloAsync;
            return testCluster;
        }

        /// <summary>
        /// Creates a cluster identifier.
        /// </summary>
        /// <returns>A new cluster identifier.</returns>
        public static string CreateClusterId()
        {
            string prefix = "testcluster-";
            int randomSuffix = Random.Shared.Next(1000);
            DateTime now = DateTime.UtcNow;
            string DateTimeFormat = @"yyyy-MM-dd\tHH-mm-ss";
            return $"{prefix}{now.ToString(DateTimeFormat, CultureInfo.InvariantCulture)}-{randomSuffix}";
        }

        private void ConfigureDefaultPorts()
        {
            // Set base ports if none are currently set.
            (int baseSiloPort, int baseGatewayPort) = this.PortAllocator.AllocateConsecutivePortPairs(this.Options.InitialSilosCount + 3);
            if (this.Options.BaseSiloPort == 0) this.Options.BaseSiloPort = baseSiloPort;
            if (this.Options.BaseGatewayPort == 0) this.Options.BaseGatewayPort = baseGatewayPort;
        }

        internal class ConfigureStaticClusterDeploymentOptions : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                hostBuilder.ConfigureServices((context, services) =>
                {
                    var initialSilos = int.Parse(context.Configuration[nameof(TestClusterOptions.InitialSilosCount)]);
                    var siloNames = Enumerable.Range(0, initialSilos).Select(GetSiloName).ToList();
                    services.Configure<StaticClusterDeploymentOptions>(options => options.SiloNames = siloNames);
                });
            }

            private static string GetSiloName(int instanceNumber)
            {
                return instanceNumber == 0 ? Silo.PrimarySiloName : $"Secondary_{instanceNumber}";
            }
        }
    }
}