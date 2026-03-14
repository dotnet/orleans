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
        public string Invariant { get; set; }

        /// <summary>
        /// Gets or sets the connection string.
        /// </summary>
        [Redact]
        public string ConnectionString { get; set; }
    }
}