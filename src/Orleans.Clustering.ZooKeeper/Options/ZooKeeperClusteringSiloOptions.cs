namespace Orleans.Configuration
{
    /// <summary>
    /// Option to configure ZooKeeperMembership
    /// </summary>
    public class ZooKeeperClusteringSiloOptions
    {
        /// <summary>
        /// Connection string for ZooKeeper Storage
        /// </summary>
        [Redact]
        public string ConnectionString { get; set; }
    }
}
