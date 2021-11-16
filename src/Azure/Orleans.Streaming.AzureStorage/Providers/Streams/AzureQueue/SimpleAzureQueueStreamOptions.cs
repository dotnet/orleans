using System;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Queues;
using Orleans.AzureUtils;

namespace Orleans.Configuration
{
    /// <summary>
    /// Simple Azure queue stream provider options.
    /// </summary>
    public class SimpleAzureQueueStreamOptions
    {
        private const string DeprecationMessage = "Use ConfigureQueueServiceClient instead. This property is deprecated.";

        /// <summary>
        /// The queue name.
        /// </summary>
        public string QueueName { get; set; }

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
        /// The optional delegate used to create a <see cref="QueueServiceClient"/> instance.
        /// </summary>
        internal Func<Task<QueueServiceClient>> CreateClient { get; private set; }

        /// <summary>
        /// The message visibility timeout.
        /// </summary>
        public TimeSpan? MessageVisibilityTimeout { get; set; }

        /// <summary>
        /// Deprecated: use ConfigureQueueServiceClient instead.
        /// </summary>
        [Obsolete(DeprecationMessage, error: true)]
        public string ConnectionString { get => throw new NotSupportedException(DeprecationMessage); set => throw new NotSupportedException(DeprecationMessage); }

        /// <summary>
        /// Deprecated: use ConfigureQueueServiceClient instead.
        /// </summary>
        [Obsolete(DeprecationMessage, error: true)]
        public TokenCredential TokenCredential { get => throw new NotSupportedException(DeprecationMessage); set => throw new NotSupportedException(DeprecationMessage); }

        /// <summary>
        /// Deprecated: use ConfigureQueueServiceClient instead.
        /// </summary>
        [Obsolete(DeprecationMessage, error: true)]
        public Uri ServiceUri { get => throw new NotSupportedException(DeprecationMessage); set => throw new NotSupportedException(DeprecationMessage); }

        /// <summary>
        /// Configures the <see cref="QueueServiceClient"/> using a connection string.
        /// </summary>
        public void ConfigureQueueServiceClient(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentNullException(nameof(connectionString));
            CreateClient = () => Task.FromResult(new QueueServiceClient(connectionString, ClientOptions));
        }

        /// <summary>
        /// Configures the <see cref="QueueServiceClient"/> using an authenticated service URI.
        /// </summary>
        public void ConfigureQueueServiceClient(Uri serviceUri)
        {
            if (serviceUri is null) throw new ArgumentNullException(nameof(serviceUri));
            CreateClient = () => Task.FromResult(new QueueServiceClient(serviceUri, ClientOptions));
        }

        /// <summary>
        /// Configures the <see cref="QueueServiceClient"/> using the provided callback.
        /// </summary>
        public void ConfigureQueueServiceClient(Func<Task<QueueServiceClient>> createClientCallback)
        {
            CreateClient = createClientCallback ?? throw new ArgumentNullException(nameof(createClientCallback));
        }

        /// <summary>
        /// Configures the <see cref="QueueServiceClient"/> using an authenticated service URI and a <see cref="Azure.Core.TokenCredential"/>.
        /// </summary>
        public void ConfigureQueueServiceClient(Uri serviceUri, TokenCredential tokenCredential)
        {
            if (serviceUri is null) throw new ArgumentNullException(nameof(serviceUri));
            if (tokenCredential is null) throw new ArgumentNullException(nameof(tokenCredential));
            CreateClient = () => Task.FromResult(new QueueServiceClient(serviceUri, tokenCredential, ClientOptions));
        }

        /// <summary>
        /// Configures the <see cref="QueueServiceClient"/> using an authenticated service URI and a <see cref="Azure.AzureSasCredential"/>.
        /// </summary>
        public void ConfigureQueueServiceClient(Uri serviceUri, AzureSasCredential azureSasCredential)
        {
            if (serviceUri is null) throw new ArgumentNullException(nameof(serviceUri));
            if (azureSasCredential is null) throw new ArgumentNullException(nameof(azureSasCredential));
            CreateClient = () => Task.FromResult(new QueueServiceClient(serviceUri, azureSasCredential, ClientOptions));
        }

        /// <summary>
        /// Configures the <see cref="QueueServiceClient"/> using an authenticated service URI and a <see cref="StorageSharedKeyCredential"/>.
        /// </summary>
        public void ConfigureQueueServiceClient(Uri serviceUri, StorageSharedKeyCredential sharedKeyCredential)
        {
            if (serviceUri is null) throw new ArgumentNullException(nameof(serviceUri));
            if (sharedKeyCredential is null) throw new ArgumentNullException(nameof(sharedKeyCredential));
            CreateClient = () => Task.FromResult(new QueueServiceClient(serviceUri, sharedKeyCredential, ClientOptions));
        }
    }
}
