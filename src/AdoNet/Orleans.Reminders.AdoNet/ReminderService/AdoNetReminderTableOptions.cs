using System.Data.Common;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for ADO.NET reminder storage.
    /// </summary>
    public class AdoNetReminderTableOptions
    {
        /// <summary>
        /// Gets or sets the ADO.NET invariant.
        /// </summary>
        public string Invariant { get; set; } = null!;

        /// <summary>
        /// Gets or sets the connection string.
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
    }
}
