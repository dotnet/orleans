using System;
using Azure.Core;

namespace Orleans.Configuration
{
    /// <summary>
    /// Simple Azure queue stream provider options.
    /// </summary>
    public class SimpleAzureQueueStreamOptions
    {
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// The Service URI (e.g. https://x.queue.core.windows.net). Required for specifying <see cref="TokenCredential"/>.
        /// </summary>
        public Uri ServiceUri { get; set; }

        /// <summary>
        /// Use AAD to access the storage account
        /// </summary>
        public TokenCredential TokenCredential { get; set; }

        public string QueueName { get; set; }
    }
}
