using System;
using System.Collections.Generic;
using Azure.Core;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Azure queue stream provider options.
    /// </summary>
    public class AzureQueueOptions
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

        public TimeSpan? MessageVisibilityTimeout { get; set; }

        public List<string> QueueNames { get; set; }
    }

    public class AzureQueueOptionsValidator : IConfigurationValidator
    {
        private readonly AzureQueueOptions options;
        private readonly string name;

        private AzureQueueOptionsValidator(AzureQueueOptions options, string name)
        {
            this.options = options;
            this.name = name;
        }

        public void ValidateConfiguration()
        {
            if (this.options.ServiceUri == null)
            {
                if (this.options.TokenCredential != null)
                {
                    throw new OrleansConfigurationException($"{nameof(AzureQueueOptions)} on stream provider {name} is invalid. {nameof(options.ServiceUri)} is required for {nameof(options.TokenCredential)}");
                }

                if (String.IsNullOrEmpty(options.ConnectionString))
                    throw new OrleansConfigurationException(
                        $"{nameof(AzureQueueOptions)} on stream provider {this.name} is invalid. {nameof(AzureQueueOptions.ConnectionString)} is invalid");
            }

            if (options.QueueNames == null || options.QueueNames.Count == 0)
                throw new OrleansConfigurationException(
                    $"{nameof(AzureQueueOptions)} on stream provider {this.name} is invalid. {nameof(AzureQueueOptions.QueueNames)} is invalid");
        }

        public static IConfigurationValidator Create(IServiceProvider services, string name)
        {
            AzureQueueOptions aqOptions = services.GetOptionsByName<AzureQueueOptions>(name);
            return new AzureQueueOptionsValidator(aqOptions, name);
        }
    }
}
