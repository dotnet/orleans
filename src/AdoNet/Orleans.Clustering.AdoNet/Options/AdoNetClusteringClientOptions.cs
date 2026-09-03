using System.Data.Common;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures relational database access for client gateway discovery.
    /// </summary>
    public class AdoNetClusteringClientOptions
    {
        /// <summary>
        /// Connection string for Sql
        /// </summary>
        [Redact]
        public string? ConnectionString { get; set; }

        /// <summary>
        /// Gets or sets the data source used to open database connections.
        /// </summary>
        /// <remarks>
        /// The data source is owned by the caller and is not disposed by Orleans.
        /// </remarks>
        [Redact]
        public DbDataSource? DataSource { get; set; }

        /// <summary>
        /// The invariant name of the connector for gatewayProvider's database.
        /// </summary>
        public string Invariant { get; set; } = null!;
    }
}
