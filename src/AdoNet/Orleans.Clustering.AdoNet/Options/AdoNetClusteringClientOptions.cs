namespace Orleans.Configuration
{
    public class AdoNetClusteringClientOptions
    {
        /// <summary>
        /// Connection string for Sql
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// The invariant name of the connector for gatewayProvider's database.
        /// </summary>
        public string AdoInvariant { get; set; }
    }
}
