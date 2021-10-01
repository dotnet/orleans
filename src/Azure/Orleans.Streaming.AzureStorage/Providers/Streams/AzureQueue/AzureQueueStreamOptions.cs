using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Queues;
using Orleans.AzureUtils;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Azure queue stream provider options.
    /// </summary>
    public class AzureQueueOptions
    {
        /// <summary>
        /// The service connection string.
        /// </summary>
        /// <remarks>
        /// This property is superseded by all other properties except for <see cref="ServiceUri"/>.
        /// </remarks>
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// The Service URI (e.g. https://x.queue.core.windows.net). Required for specifying <see cref="TokenCredential"/>.
        /// </summary>
        /// <remarks>
        /// If this property contains a shared access signature, then no other credential properties are required.
        /// Otherwise, the presence of any other credential property will take precedence over this.
        /// </remarks>
        public Uri ServiceUri { get; set; }

        /// <summary>
        /// Token credentials, to be used in conjunction with <see cref="ServiceUri"/>.
        /// </summary>
        /// <remarks>
        /// This property takes precedence over specifying only <see cref="ServiceUri"/> and over <see cref="ConnectionString"/>, <see cref="AzureSasCredential"/>, and <see cref="SharedKeyCredential"/>.
        /// This property is superseded by <see cref="CreateClient"/>.
        /// </remarks>
        public TokenCredential TokenCredential { get; set; }

        /// <summary>
        /// Azure SAS credentials, to be used in conjunction with <see cref="ServiceUri"/>.
        /// </summary>
        /// <remarks>
        /// This property takes precedence over specifying only <see cref="ServiceUri"/> and over <see cref="ConnectionString"/> and <see cref="SharedKeyCredential"/>.
        /// This property is superseded by <see cref="CreateClient"/> and <see cref="TokenCredential"/>.
        /// </remarks>
        public AzureSasCredential AzureSasCredential { get; set; }

        /// <summary>
        /// Options to be used when configuring the queue storage client, or <see langword="null"/> to use the default options.
        /// </summary>
        public QueueClientOptions ClientOptions { get; set; } = new QueueClientOptions
        {
            Retry =
            {
                Mode = RetryMode.Fixed,
                Delay = AzureQueueDefaultPolicies.PauseBetweenQueueOperationRetries,
                MaxRetries = AzureQueueDefaultPolicies.MaxQueueOperationRetries,
                NetworkTimeout = AzureQueueDefaultPolicies.QueueOperationTimeout,
            }
        };

        /// <summary>
        /// Shared key credentials, to be used in conjunction with <see cref="ServiceUri"/>.
        /// </summary>
        /// <remarks>
        /// This property takes precedence over specifying only <see cref="ServiceUri"/> and over <see cref="ConnectionString"/>.
        /// This property is superseded by <see cref="CreateClient"/>, <see cref="TokenCredential"/>, and <see cref="AzureSasCredential"/>.
        /// </remarks>
        public StorageSharedKeyCredential SharedKeyCredential { get; set; }

        /// <summary>
        /// The optional delegate used to create a <see cref="QueueServiceClient"/> instance.
        /// </summary>
        /// <remarks>
        /// This property, if not <see langword="null"/>, takes precedence over <see cref="ConnectionString"/>, <see cref="SharedKeyCredential"/>, <see cref="AzureSasCredential"/>, <see cref="TokenCredential"/>, <see cref="ClientOptions"/>, and <see cref="ServiceUri"/>,
        /// </remarks>
        public Func<Task<QueueServiceClient>> CreateClient { get; set; }

        public TimeSpan? MessageVisibilityTimeout { get; set; }

        public List<string> QueueNames { get; set; }

        /// <summary>
        /// Sets credential properties using an authenticated service URI.
        /// </summary>
        /// <param name="serviceUri"></param>
        public void SetCredentials(Uri serviceUri)
        {
            ClearCredentials();
            ServiceUri = serviceUri ?? throw new ArgumentNullException(nameof(serviceUri));
        }

        /// <summary>
        /// Sets credential properties using a connection string.
        /// </summary>
        public void SetCredentials(string connectionString)
        {
            ClearCredentials();
            ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <summary>
        /// Sets credential properties using an authenticated service URI and a <see cref="Azure.Core.TokenCredential"/>.
        /// </summary>
        public void SetCredentials(Uri serviceUri, TokenCredential tokenCredential)
        {
            ClearCredentials();
            ServiceUri = serviceUri ?? throw new ArgumentNullException(nameof(serviceUri));
            TokenCredential = tokenCredential ?? throw new ArgumentNullException(nameof(tokenCredential));
        }

        /// <summary>
        /// Sets credential properties using an authenticated service URI and a <see cref="Azure.AzureSasCredential"/>.
        /// </summary>
        public void SetCredentials(Uri serviceUri, AzureSasCredential azureSasCredential)
        {
            ClearCredentials();
            ServiceUri = serviceUri ?? throw new ArgumentNullException(nameof(serviceUri));
            AzureSasCredential = azureSasCredential ?? throw new ArgumentNullException(nameof(azureSasCredential));
        }

        /// <summary>
        /// Sets credential properties using an authenticated service URI and a <see cref="StorageSharedKeyCredential"/>.
        /// </summary>
        public void SetCredentials(Uri serviceUri, StorageSharedKeyCredential sharedKeyCredential)
        {
            ClearCredentials();
            ServiceUri = serviceUri ?? throw new ArgumentNullException(nameof(serviceUri));
            SharedKeyCredential = sharedKeyCredential ?? throw new ArgumentNullException(nameof(sharedKeyCredential));
        }

        private void ClearCredentials()
        {
            ServiceUri = default;
            TokenCredential = default;
            ConnectionString = default;
            AzureSasCredential = default;
            SharedKeyCredential = default;
        }
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
