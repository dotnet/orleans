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
        /// The DNS host name of this silo.
        /// This is a true host name, no IP address. Equals Dns.GetHostName().
        /// </summary>
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