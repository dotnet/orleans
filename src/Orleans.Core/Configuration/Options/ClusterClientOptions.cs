namespace Orleans.Runtime
{
    /// <summary>
    /// Configures the Orleans cluster client.
    /// </summary>
    public class ClusterClientOptions
    {
        /// <summary>
        /// Gets or sets the cluster identity. This used to be called DeploymentId before Orleans 2.0 name.
        /// </summary>
        public string ClusterId { get; set; }
    }
}
