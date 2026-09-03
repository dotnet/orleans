namespace Orleans.Providers
{
    /// <summary>
    /// Constant values used by providers.
    /// </summary>
    public static class ProviderConstants
    {
        /// <summary>
        /// The default storage provider name.
        /// </summary>
        public const string DEFAULT_STORAGE_PROVIDER_NAME = "Default";

        /// <summary>
        /// The default log consistency provider name.
        /// </summary>
        public const string DEFAULT_LOG_CONSISTENCY_PROVIDER_NAME = "Default";

        /// <summary>
        /// The default grain storage provider name used by streaming pub/sub.
        /// </summary>
        public const string DEFAULT_PUBSUB_PROVIDER_NAME = "PubSubStore";
    }
}
