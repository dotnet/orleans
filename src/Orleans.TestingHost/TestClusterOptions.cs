using System.Collections.Generic;
using System.Net;
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
        /// Gets or sets a value indicating what transport to use for connecting silos and clients.
        /// </summary>
        /// <remarks>
        /// Defaults to InMemory.
        /// </remarks>
        public ConnectionTransportType ConnectionTransport { get; set; } = ConnectionTransportType.InMemory;

        /// <summary>
        /// Converts these options into a dictionary.
        /// </summary>
        /// <returns>The options dictionary.</returns>
        public Dictionary<string, string> ToDictionary()
        {
            var result = new Dictionary<string, string>
            {
                [$"Orleans:{nameof(ClusterId)}"] = this.ClusterId,
                [$"Orleans:{nameof(ServiceId)}"] = this.ServiceId,
                [nameof(BaseSiloPort)] = this.BaseSiloPort.ToString(),
                [nameof(BaseGatewayPort)] = this.BaseGatewayPort.ToString(),
                [nameof(UseTestClusterMembership)] = this.UseTestClusterMembership.ToString(),
                [nameof(InitializeClientOnDeploy)] = this.InitializeClientOnDeploy.ToString(),
                [nameof(InitialSilosCount)] = this.InitialSilosCount.ToString(),
                [nameof(ApplicationBaseDirectory)] = this.ApplicationBaseDirectory,
                [nameof(ConfigureFileLogging)] = this.ConfigureFileLogging.ToString(),
                [nameof(AssumeHomogenousSilosForTesting)] = this.AssumeHomogenousSilosForTesting.ToString(),
                [nameof(GatewayPerSilo)] = this.GatewayPerSilo.ToString(),
                [nameof(ConnectionTransport)] = this.ConnectionTransport.ToString(),
            };

            if (UseTestClusterMembership)
            {
                result["Orleans:Clustering:ProviderType"] = "Development";
            }
            
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
        public IPEndPoint PrimarySiloEndPoint { get; set; }

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
            var result = new TestSiloSpecificOptions
            {
                SiloName = testClusterOptions.UseTestClusterMembership && instanceNumber == 0 ? Silo.PrimarySiloName : $"Secondary_{instanceNumber}",
                PrimarySiloEndPoint = testClusterOptions.UseTestClusterMembership ? new IPEndPoint(IPAddress.Loopback, testClusterOptions.BaseSiloPort) : null,
            };

            if (assignNewPort)
            {
                var (siloPort, gatewayPort) = testCluster.PortAllocator.AllocateConsecutivePortPairs(1);
                result.SiloPort = siloPort;
                result.GatewayPort = (instanceNumber == 0 || testClusterOptions.GatewayPerSilo) ? gatewayPort : 0;
            }
            else
            {
                result.SiloPort = testClusterOptions.BaseSiloPort + instanceNumber;
                result.GatewayPort = (instanceNumber == 0 || testClusterOptions.GatewayPerSilo) ? testClusterOptions.BaseGatewayPort + instanceNumber : 0;
            }

            return result;
        }

        /// <summary>
        /// Converts these options into a dictionary.
        /// </summary>
        /// <returns>The options dictionary.</returns>
        public Dictionary<string, string> ToDictionary()
        {
            var result = new Dictionary<string, string>
            {
                [$"Orleans:Endpoints:AdvertisedIPAddress"] = IPAddress.Loopback.ToString(),
                [$"Orleans:Endpoints:{nameof(SiloPort)}"] = this.SiloPort.ToString(),
                [$"Orleans:EndPoints:{nameof(GatewayPort)}"] = this.GatewayPort.ToString(),
                ["Orleans:Name"] = this.SiloName,
            };

            if (PrimarySiloEndPoint != null)
            {
                result[$"Orleans:Clustering:{nameof(PrimarySiloEndPoint)}"] = this.PrimarySiloEndPoint.ToString();
            }

            return result;
        }
    }

    /// <summary>
    /// Describe a transport method
    /// </summary>
    public enum ConnectionTransportType
    {
        /// <summary>
        /// Uses real TCP socket.
        /// </summary>
        TcpSocket = 0,

        /// <summary>
        /// Uses in memory socket.
        /// </summary>
        InMemory = 1,

        /// <summary>
        /// Uses in Unix socket.
        /// </summary>
        UnixSocket = 2,
    }
}
