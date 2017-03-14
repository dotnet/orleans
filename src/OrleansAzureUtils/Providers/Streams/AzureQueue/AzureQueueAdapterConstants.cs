
namespace Orleans.Providers.Streams.AzureQueue
{
    /// <summary>
    /// Azure queue stream provider constants.
    /// </summary>
    public static class AzureQueueAdapterConstants
    {
        internal const int CacheSizeDefaultValue = 4096;

        /// <summary>"DataConnectionString".</summary>
        public const string DataConnectionStringPropertyName = "DataConnectionString";
        /// <summary>"DeploymentId".</summary>
        public const string DeploymentIdPropertyName = "DeploymentId";
        /// <summary>"MessageVisibilityTimeout".</summary>
        public const string MessageVisibilityTimeoutPropertyName = "VisibilityTimeout";

        /// <summary>"NumQueues".</summary>
        public const string NumQueuesPropertyName = "NumQueues";
        /// <summary> Default number of Azure Queue used in this stream provider.</summary>
        public const int NumQueuesDefaultValue = 8; // keep as power of 2.
    }
}
