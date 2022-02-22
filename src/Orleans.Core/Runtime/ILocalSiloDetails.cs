namespace Orleans.Runtime
{
    /// <summary>
    /// Details of the local silo.
    /// </summary>
    public interface ILocalSiloDetails
    {
        /// <summary>
        /// Gets the name of this silo.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the cluster identity. This used to be called DeploymentId before Orleans 2.0 name.
        /// </summary>
        string ClusterId { get; }

        /// <summary>
        /// Gets the host name of this silo.
        /// </summary>
        /// <remarks>
        /// This is equal to <see cref="System.Net.Dns.GetHostName()"/>.
        /// </remarks>
        string DnsHostName { get; }

        /// <summary>
        /// Gets the address of this silo's inter-silo endpoint.
        /// </summary>
        SiloAddress SiloAddress { get; }

        /// <summary>
        /// Gets the address of this silo's gateway proxy endpoint.
        /// </summary>
        SiloAddress GatewayAddress { get; }
    }
}