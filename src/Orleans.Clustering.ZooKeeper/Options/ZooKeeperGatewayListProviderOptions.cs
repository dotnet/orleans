namespace Orleans.Configuration
{
    /// <summary>
    /// Configures ZooKeeper access for client gateway discovery.
    /// </summary>
    public class ZooKeeperGatewayListProviderOptions
    {
        /// <summary>
        /// Connection string for ZooKeeper storage
        /// </summary>
        [Redact]
        public string ConnectionString { get; set; } = null!;
    }
}
