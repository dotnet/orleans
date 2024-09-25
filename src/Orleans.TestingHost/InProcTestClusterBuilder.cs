using System;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.TestingHost;

/// <summary>Configuration builder for starting a <see cref="InProcessTestCluster"/>.</summary>
public sealed class InProcessTestClusterBuilder
{
    /// <summary>
    /// Initializes a new instance of <see cref="InProcessTestClusterBuilder"/> using the default options.
    /// </summary>
    public InProcessTestClusterBuilder()
        : this(2)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="InProcessTestClusterBuilder"/> overriding the initial silos count.
    /// </summary>
    /// <param name="initialSilosCount">The number of initial silos to deploy.</param>
    public InProcessTestClusterBuilder(short initialSilosCount)
    {
        Options = new InProcessTestClusterOptions
        {
            InitialSilosCount = initialSilosCount,
            ClusterId = CreateClusterId(),
            ServiceId = Guid.NewGuid().ToString("N"),
            UseTestClusterMembership = true,
            InitializeClientOnDeploy = true,
            ConfigureFileLogging = true,
            AssumeHomogenousSilosForTesting = true
        };
    }

    /// <summary>
    /// Gets the options.
    /// </summary>
    /// <value>The options.</value>
    public InProcessTestClusterOptions Options { get; }

    /// <summary>
    /// The port allocator.
    /// </summary>
    public ITestClusterPortAllocator PortAllocator { get; } = new TestClusterPortAllocator();

    /// <summary>
    /// Adds a delegate for configuring silo and client hosts.
    /// </summary>
    public InProcessTestClusterBuilder ConfigureHost(Action<IHostApplicationBuilder> configureDelegate)
    {
        Options.SiloHostConfigurationDelegates.Add((_, hostBuilder) => configureDelegate(hostBuilder));
        Options.ClientHostConfigurationDelegates.Add(configureDelegate);
        return this;
    }

    /// <summary>
    /// Adds a delegate to configure silos.
    /// </summary>
    /// <returns>The builder.</returns>
    public InProcessTestClusterBuilder ConfigureSilo(Action<InProcessTestSiloSpecificOptions, ISiloBuilder> configureSiloDelegate)
    {
        Options.SiloHostConfigurationDelegates.Add((options, hostBuilder) => hostBuilder.UseOrleans(siloBuilder => configureSiloDelegate(options, siloBuilder)));
        return this;
    }

    /// <summary>
    /// Adds a delegate to configure silo hosts.
    /// </summary>
    /// <returns>The builder.</returns>
    public InProcessTestClusterBuilder ConfigureSiloHost(Action<InProcessTestSiloSpecificOptions, IHostApplicationBuilder> configureSiloHostDelegate)
    {
        Options.SiloHostConfigurationDelegates.Add(configureSiloHostDelegate);
        return this;
    }

    /// <summary>
    /// Adds a delegate to configure clients.
    /// </summary>
    /// <returns>The builder.</returns>
    public InProcessTestClusterBuilder ConfigureClient(Action<IClientBuilder> configureClientDelegate)
    {
        Options.ClientHostConfigurationDelegates.Add(hostBuilder => hostBuilder.UseOrleansClient(clientBuilder => configureClientDelegate(clientBuilder)));
        return this;
    }

    /// <summary>
    /// Adds a delegate to configure clients hosts.
    /// </summary>
    /// <returns>The builder.</returns>
    public InProcessTestClusterBuilder ConfigureClientHost(Action<IHostApplicationBuilder> configureHostDelegate)
    {
        Options.ClientHostConfigurationDelegates.Add(hostBuilder => configureHostDelegate(hostBuilder));
        return this;
    }

    /// <summary>
    /// Builds this instance.
    /// </summary>
    /// <returns>InProcessTestCluster.</returns>
    public InProcessTestCluster Build()
    {
        var portAllocator = PortAllocator;

        ConfigureDefaultPorts();

        var testCluster = new InProcessTestCluster(Options, portAllocator);
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
        (int baseSiloPort, int baseGatewayPort) = PortAllocator.AllocateConsecutivePortPairs(Options.InitialSilosCount + 3);
        if (Options.BaseSiloPort == 0) Options.BaseSiloPort = baseSiloPort;
        if (Options.BaseGatewayPort == 0) Options.BaseGatewayPort = baseGatewayPort;
    }

    internal class ConfigureStaticClusterDeploymentOptions : IHostConfigurator
    {
        public void Configure(IHostBuilder hostBuilder)
        {
            hostBuilder.ConfigureServices((context, services) =>
            {
                var initialSilos = int.Parse(context.Configuration[nameof(InProcessTestClusterOptions.InitialSilosCount)]);
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