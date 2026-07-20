namespace Orleans.Configuration
{
    public class AdoNetClusteringClientOptions
    {
        /// <summary>
        /// Connection string for Sql
        /// </summary>
        [Redact]
        public string ConnectionString { get; set; } = null!;

        /// <summary>
        /// The invariant name of the connector for gatewayProvider's database.
        /// </summary>
        public string Invariant { get; set; } = null!;
    }
}
