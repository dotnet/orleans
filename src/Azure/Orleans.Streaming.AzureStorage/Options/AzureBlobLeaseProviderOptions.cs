using System;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Streaming.AzureStorage;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for configuring a lease provider backed by Azure Blob Storage leases.
    /// </summary>
    public class AzureBlobLeaseProviderOptions
    {
        private BlobServiceClient _blobServiceClient;

        public string BlobContainerName { get; set; } = DefaultBlobContainerName;
        public const string DefaultBlobContainerName = "Leases";

        /// <summary>
        /// Options to be used when configuring the blob storage client, or <see langword="null"/> to use the default options.
        /// </summary>
        [Obsolete($"Set the {nameof(BlobServiceClient)} property directly.")]
        public BlobClientOptions ClientOptions { get; set; }

        /// <summary>
        /// The optional delegate used to create a <see cref="BlobServiceClient"/> instance.
        /// </summary>
        internal Func<Task<BlobServiceClient>> CreateClient { get; private set; }

        /// <summary>
        /// Gets or sets the client used to access the Azure Blob Service.
        /// </summary>
        public BlobServiceClient BlobServiceClient
        {
            get => _blobServiceClient;
            set
            {
                _blobServiceClient = value;
                CreateClient = () => Task.FromResult(value);
            }
        }

        /// <summary>
        /// Configures the <see cref="BlobServiceClient"/> using a connection string.
        /// </summary>
        [Obsolete($"Set the {nameof(BlobServiceClient)} property directly.")]
        public void ConfigureBlobServiceClient(string connectionString)
        {
            BlobServiceClient = new BlobServiceClient(connectionString, ClientOptions);
        }

        /// <summary>
        /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI.
        /// </summary>
        [Obsolete($"Set the {nameof(BlobServiceClient)} property directly.")]
        public void ConfigureBlobServiceClient(Uri serviceUri)
        {
            BlobServiceClient = new BlobServiceClient(serviceUri, ClientOptions);
        }

        /// <summary>
        /// Configures the <see cref="BlobServiceClient"/> using the provided callback.
        /// </summary>
        [Obsolete($"Set the {nameof(BlobServiceClient)} property directly.")]
        public void ConfigureBlobServiceClient(Func<Task<BlobServiceClient>> createClientCallback)
        {
            CreateClient = createClientCallback ?? throw new ArgumentNullException(nameof(createClientCallback));
        }

        /// <summary>
        /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI and a <see cref="Azure.Core.TokenCredential"/>.
        /// </summary>
        [Obsolete($"Set the {nameof(BlobServiceClient)} property directly.")]
        public void ConfigureBlobServiceClient(Uri serviceUri, TokenCredential tokenCredential)
        {
            BlobServiceClient = new BlobServiceClient(serviceUri, tokenCredential, ClientOptions);
        }

        /// <summary>
        /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI and a <see cref="Azure.AzureSasCredential"/>.
        /// </summary>
        [Obsolete($"Set the {nameof(BlobServiceClient)} property directly.")]
        public void ConfigureBlobServiceClient(Uri serviceUri, AzureSasCredential azureSasCredential)
        {
            BlobServiceClient = new BlobServiceClient(serviceUri, azureSasCredential, ClientOptions);
        }

        /// <summary>
        /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI and a <see cref="StorageSharedKeyCredential"/>.
        /// </summary>
        [Obsolete($"Set the {nameof(BlobServiceClient)} property directly.")]
        public void ConfigureBlobServiceClient(Uri serviceUri, StorageSharedKeyCredential sharedKeyCredential)
        {
            BlobServiceClient = new BlobServiceClient(serviceUri, sharedKeyCredential, ClientOptions);
        }
    }

    /// <summary>
    /// Configuration validator for AzureBlobLeaseProviderOptions
    /// </summary>
    public class AzureBlobLeaseProviderOptionsValidator : IConfigurationValidator
    {
        private readonly AzureBlobLeaseProviderOptions options;
        private readonly string name;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="options">The option to be validated.</param>
        public AzureBlobLeaseProviderOptionsValidator(IOptions<AzureBlobLeaseProviderOptions> options)
        {
            this.options = options.Value;
        }

        /// <summary>
        /// Creates creates validator for named instance of AzureBlobLeaseProviderOptions options.
        /// </summary>
        /// <param name="services"></param>
        /// <param name="name"></param>
        /// <returns></returns>
        public static IConfigurationValidator Create(IServiceProvider services, string name)
        {
            var options = services.GetOptionsByName<AzureBlobLeaseProviderOptions>(name);
            return new AzureBlobLeaseProviderOptionsValidator(options, name);
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="options">The option to be validated.</param>
        /// <param name="name">The option name to be validated.</param>
        private AzureBlobLeaseProviderOptionsValidator(AzureBlobLeaseProviderOptions options, string name)
        {
            this.options = options;
            this.name = name ?? string.Empty;
        }

        public void ValidateConfiguration()
        {
            // name can be null, but not empty or white space.
            if(this.name != null && string.IsNullOrWhiteSpace(this.name))
            {
                throw new OrleansConfigurationException($"Named option {nameof(AzureBlobLeaseProviderOptions)} of name {this.name} is invalid.  Name cannot be empty or whitespace.");
            }

            if (this.options.CreateClient is null)
            {
                throw new OrleansConfigurationException($"No credentials specified for Azure Blob Service lease provider \"{name}\". Use the {options.GetType().Name}.{nameof(AzureBlobLeaseProviderOptions.ConfigureBlobServiceClient)} method to configure the Azure Blob Service client.");
            }

            try
            {
                AzureBlobUtils.ValidateContainerName(this.options.BlobContainerName);
            }
            catch (ArgumentException e)
            {
                var errorStr = string.IsNullOrEmpty(this.name)
                    ? $"Configuration for {nameof(AzureBlobLeaseProviderOptions)} {this.name} is invalid. {nameof(this.options.BlobContainerName)} is not valid"
                    : $"Configuration for {nameof(AzureBlobLeaseProviderOptions)} is invalid. {nameof(this.options.BlobContainerName)} is not valid";
                throw new OrleansConfigurationException(errorStr , e);
            }
        }
    }

}
