using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

            this.AddClientBuilderConfigurator<AddTestHooksApplicationParts>();
            this.AddSiloBuilderConfigurator<AddTestHooksApplicationParts>();
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

        public TestClusterOptions Options { get; }

        /// <summary>
        /// Delegate used to create and start an individual silo.
        /// </summary>
        public Func<string, IConfiguration, Task<SiloHandle>> CreateSiloAsync { private get; set; }

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
        /// Adds an implementation of <see cref="ISiloConfigurator"/>, <see cref="IHostConfigurator"/>, or <see cref="ISiloBuilderConfigurator"/> to configure silos created by the test cluster.
        /// </summary>
        /// <typeparam name="T">The configurator type.</typeparam>
        public TestClusterBuilder AddSiloBuilderConfigurator<T>() where T : new()
        {
#pragma warning disable CS0618 // Type or member is obsolete
            if (!typeof(ISiloConfigurator).IsAssignableFrom(typeof(T)) && !typeof(IHostConfigurator).IsAssignableFrom(typeof(T)) && !typeof(ISiloBuilderConfigurator).IsAssignableFrom(typeof(T)))
            {
                throw new ArgumentException($"The type {typeof(T)} is not assignable to either {nameof(ISiloConfigurator)}, {nameof(IHostConfigurator)}, or {nameof(ISiloBuilderConfigurator)}.");
            }
#pragma warning restore CS0618 // Type or member is obsolete

            this.Options.SiloBuilderConfiguratorTypes.Add(typeof(T).AssemblyQualifiedName);
            return this;
        }

        public TestClusterBuilder AddClientBuilderConfigurator<TClientBuilderConfigurator>() where TClientBuilderConfigurator : IClientBuilderConfigurator, new()
        {
            this.Options.ClientBuilderConfiguratorTypes.Add(typeof(TClientBuilderConfigurator).AssemblyQualifiedName);
            return this;
        }

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
            if (this.CreateSiloAsync != null) testCluster.CreateSiloAsync = this.CreateSiloAsync;
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
            (int baseSiloPort, int baseGatewayPort) = this.PortAllocator.AllocateConsecutivePortPairs(this.Options.InitialSilosCount + 3);
            if (this.Options.BaseSiloPort == 0) this.Options.BaseSiloPort = baseSiloPort;
            if (this.Options.BaseGatewayPort == 0) this.Options.BaseGatewayPort = baseGatewayPort;
        }

        internal class AddTestHooksApplicationParts : IClientBuilderConfigurator, ISiloConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(ITestHooksSystemTarget).Assembly));
            }

            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.ConfigureApplicationParts(parts => parts.AddFrameworkPart(typeof(ITestHooksSystemTarget).Assembly));
            }
        }

        internal class ConfigureStaticClusterDeploymentOptions : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
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