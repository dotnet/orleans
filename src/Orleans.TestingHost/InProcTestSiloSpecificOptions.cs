namespace Orleans.TestingHost;

/// <summary>
/// Configuration overrides for individual silos.
/// </summary>
public sealed class InProcessTestSiloSpecificOptions
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
    /// Creates an instance of the <see cref="TestSiloSpecificOptions"/> class.
    /// </summary>
    /// <param name="testCluster">The test cluster.</param>
    /// <param name="testClusterOptions">The test cluster options.</param>
    /// <param name="instanceNumber">The instance number.</param>
    /// <param name="assignNewPort">if set to <see langword="true" />, assign a new port for the silo.</param>
    /// <returns>The options.</returns>
    public static InProcessTestSiloSpecificOptions Create(InProcessTestCluster testCluster, InProcessTestClusterOptions testClusterOptions, int instanceNumber, bool assignNewPort = false)
    {
        var result = new InProcessTestSiloSpecificOptions
        {
            SiloName = $"Silo_{instanceNumber}",
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
}
