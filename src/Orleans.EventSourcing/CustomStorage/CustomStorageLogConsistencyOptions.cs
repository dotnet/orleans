namespace Orleans.Configuration
{
    /// <summary>
    /// Configures a custom-storage log consistency provider.
    /// </summary>
    public class CustomStorageLogConsistencyOptions
    {
        /// <summary>
        /// Gets or sets the identifier of the cluster which accesses storage directly.
        /// When unset, every cluster accesses storage directly.
        /// </summary>
        public string? PrimaryCluster { get; set; }
    }
}
