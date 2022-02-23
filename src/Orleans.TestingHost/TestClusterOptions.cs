using System.Collections.Generic;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Configuration options for test clusters.
    /// </summary>
    public class TestClusterOptions
    {
        /// <summary>
        /// Gets or sets the cluster identifier.
        /// </summary>
        /// <seealso cref="ClusterOptions.ClusterId"/>
        /// <value>The cluster identifier.</value>
        public string ClusterId { get; set; }

        /// <summary>
        /// Gets or sets the service identifier.
        /// </summary>
        /// <seealso cref="ClusterOptions.ServiceId"/>
        /// <value>The service identifier.</value>
        public string ServiceId { get; set; }

        /// <summary>
        /// Gets or sets the base silo port, which is the port for the first silo. Other silos will use subsequent ports.
        /// </summary>
        /// <value>The base silo port.</value>
        public int BaseSiloPort{ get; set; }

        /// <summary>
        /// Gets or sets the base gateway port, which is the gateway port for the first silo. Other silos will use subsequent ports.
        /// </summary>
        /// <value>The base gateway port.</value>
        public int BaseGatewayPort { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to use test cluster membership.
        /// </summary>
        /// <value><see langword="true" /> if test cluster membership should be used; otherwise, <see langword="false" />.</value>
        public bool UseTestClusterMembership { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to initialize the client immediately on deployment.
        /// </summary>
        /// <value><see langword="true" /> if the client should be initialized immediately on deployment; otherwise, <see langword="false" />.</value>
        public bool InitializeClientOnDeploy { get; set; }

        /// <summary>
        /// Gets or sets the initial silos count.
        /// </summary>
        /// <value>The initial silos count.</value>
        public short InitialSilosCount { get; set; }

        /// <summary>
        /// Gets or sets the application base directory.
        /// </summary>
        /// <value>The application base directory.</value>
        public string ApplicationBaseDirectory { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to configure file logging.
        /// </summary>
        /// <value><see langword="true" /> if file logging should be configured; otherwise, <see langword="false" />.</value>
        public bool ConfigureFileLogging { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether to assume homogeneous silos for testing purposes.
        /// </summary>
        /// <value><see langword="true" /> if the cluster should assume homogeneous silos; otherwise, <see langword="false" />.</value>
        public bool AssumeHomogenousSilosForTesting { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether each silo should host a gateway.
        /// </summary>
        /// <value><see langword="true" /> if each silo should host a gateway; otherwise, <see langword="false" />.</value>
        public bool GatewayPerSilo { get; set; } = true;

        /// <summary>
        /// Gets the silo builder configurator types.
        /// </summary>
        /// <value>The silo builder configurator types.</value>
        public List<string> SiloBuilderConfiguratorTypes { get; } = new List<string>();

        /// <summary>
        /// Gets the client builder configurator types.
        /// </summary>
        /// <value>The client builder configurator types.</value>
        public List<string> ClientBuilderConfiguratorTypes { get; } = new List<string>();

        /// <summary>
        /// Gets or sets a value indicating whether to use an in-memory transport for connecting silos and clients, instead of TCP.
        /// </summary>
        /// <remarks>
        /// Defaults to <see langword="true"/>
        /// </remarks>
        public bool UseInMemoryTransport { get; set; } = true;

        /// <summary>
        /// Converts these options into a dictionary.
        /// </summary>
        /// <returns>The options dictionary.</returns>
        public Dictionary<string, string> ToDictionary()
        {
            var result = new Dictionary<string, string>
            {
                [nameof(ClusterId)] = this.ClusterId,
                [nameof(ServiceId)] = this.ServiceId,
                [nameof(BaseSiloPort)] = this.BaseSiloPort.ToString(),
                [nameof(BaseGatewayPort)] = this.BaseGatewayPort.ToString(),
                [nameof(UseTestClusterMembership)] = this.UseTestClusterMembership.ToString(),
                [nameof(InitializeClientOnDeploy)] = this.InitializeClientOnDeploy.ToString(),
                [nameof(InitialSilosCount)] = this.InitialSilosCount.ToString(),
                [nameof(ApplicationBaseDirectory)] = this.ApplicationBaseDirectory,
                [nameof(ConfigureFileLogging)] = this.ConfigureFileLogging.ToString(),
                [nameof(AssumeHomogenousSilosForTesting)] = this.AssumeHomogenousSilosForTesting.ToString(),
                [nameof(GatewayPerSilo)] = this.GatewayPerSilo.ToString(),
                [nameof(UseInMemoryTransport)] = this.UseInMemoryTransport.ToString(),
            };
            
            if (this.SiloBuilderConfiguratorTypes != null)
            {
                for (int i = 0; i < this.SiloBuilderConfiguratorTypes.Count; i++)
                {
                    result[$"{nameof(SiloBuilderConfiguratorTypes)}:{i}"] = this.SiloBuilderConfiguratorTypes[i];
                }
            }

            if (this.ClientBuilderConfiguratorTypes != null)
            {
                for (int i = 0; i < this.ClientBuilderConfiguratorTypes.Count; i++)
                {
                    result[$"{nameof(ClientBuilderConfiguratorTypes)}:{i}"] = this.ClientBuilderConfiguratorTypes[i];
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Configuration overrides for individual silos.
    /// </summary>
    public class TestSiloSpecificOptions
    {
        /// <summary>
        /// Gets or sets the silo port.
        /// </summary>
        /// <value>The silo port.</value>
        public int SiloPort { get; set; }

        /// <summary>
        /// Gets or sets the gateway port.
        /// </summary>
        /// <value>The gateway port.</value>
        public int GatewayPort { get; set; }

        /// <summary>
        /// Gets or sets the name of the silo.
        /// </summary>
        /// <value>The name of the silo.</value>
        public string SiloName { get; set; }

        /// <summary>
        /// Gets or sets the primary silo port.
        /// </summary>
        /// <value>The primary silo port.</value>
        public int PrimarySiloPort { get; set; }

        /// <summary>
        /// Creates an instance of the <see cref="TestSiloSpecificOptions"/> class.
        /// </summary>
        /// <param name="testCluster">The test cluster.</param>
        /// <param name="testClusterOptions">The test cluster options.</param>
        /// <param name="instanceNumber">The instance number.</param>
        /// <param name="assignNewPort">if set to <see langword="true" />, assign a new port for the silo.</param>
        /// <returns>The options.</returns>
        public static TestSiloSpecificOptions Create(TestCluster testCluster, TestClusterOptions testClusterOptions, int instanceNumber, bool assignNewPort = false)
        {
            var siloName = testClusterOptions.UseTestClusterMembership && instanceNumber == 0
                ? Silo.PrimarySiloName
                : $"Secondary_{instanceNumber}";
            if (assignNewPort)
            {
                (int siloPort, int gatewayPort) = testCluster.PortAllocator.AllocateConsecutivePortPairs(1);
                var result = new TestSiloSpecificOptions
                {
                    SiloPort = siloPort,
                    GatewayPort = (instanceNumber == 0 || testClusterOptions.GatewayPerSilo) ? gatewayPort : 0,
                    SiloName = siloName,
                    PrimarySiloPort = testClusterOptions.UseTestClusterMembership ? testClusterOptions.BaseSiloPort : 0,
                };
                return result;
            }
            else
            {
                var result = new TestSiloSpecificOptions
                {
                    SiloPort = testClusterOptions.BaseSiloPort + instanceNumber,
                    GatewayPort = (instanceNumber == 0 || testClusterOptions.GatewayPerSilo) ? testClusterOptions.BaseGatewayPort + instanceNumber : 0,
                    SiloName = siloName,
                    PrimarySiloPort = testClusterOptions.UseTestClusterMembership ? testClusterOptions.BaseSiloPort : 0,
                };
                return result;
            }
        }

        /// <summary>
        /// Converts these options into a dictionary.
        /// </summary>
        /// <returns>The options dictionary.</returns>
        public Dictionary<string, string> ToDictionary() => new Dictionary<string, string>
        {
            [nameof(SiloPort)] = this.SiloPort.ToString(),
            [nameof(GatewayPort)] = this.GatewayPort.ToString(),
            [nameof(SiloName)] = this.SiloName,
            [nameof(PrimarySiloPort)] = this.PrimarySiloPort.ToString()
        };
    }
}
