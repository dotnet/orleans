namespace Orleans.Configuration
{
    /// <summary>
    /// Configures a custom-storage log consistency provider.
    /// </summary>
    public class CustomStorageLogConsistencyOptions
    {
        /// <summary>
        /// Gets or sets the cluster identifier passed to each custom-storage adaptor.
        /// Custom-storage adaptors accept submissions from every cluster.
        /// </summary>
        public string? PrimaryCluster { get; set; }
    }
}
