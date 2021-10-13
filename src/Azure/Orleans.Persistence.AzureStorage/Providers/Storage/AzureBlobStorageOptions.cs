using System;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Blobs;
using Newtonsoft.Json;
using Orleans.Persistence.AzureStorage;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    public class AzureBlobStorageOptions
    {
        /// <summary>
        /// Container name where grain stage is stored
        /// </summary>
        public string ContainerName { get; set; } = DEFAULT_CONTAINER_NAME;
        public const string DEFAULT_CONTAINER_NAME = "grainstate";

        /// <summary>
        /// Options to be used when configuring the blob storage client, or <see langword="null"/> to use the default options.
        /// </summary>
        public BlobClientOptions ClientOptions { get; set; }

        /// <summary>
        /// The optional delegate used to create a <see cref="BlobServiceClient"/> instance.
        /// </summary>
        internal Func<Task<BlobServiceClient>> CreateClient { get; private set; }

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        public bool UseJson { get; set; }
        public bool UseFullAssemblyNames { get; set; }
        public bool IndentJson { get; set; }
        public TypeNameHandling? TypeNameHandling { get; set; }
        public Action<JsonSerializerSettings> ConfigureJsonSerializerSettings { get; set; }

        /// <summary>
        /// Configures the <see cref="BlobServiceClient"/> using a connection string.
        /// </summary>
        public void ConfigureBlobServiceClient(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentNullException(nameof(connectionString));
            CreateClient = () => Task.FromResult(new BlobServiceClient(connectionString, ClientOptions));
        }

        /// <summary>
        /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI.
        /// </summary>
        public void ConfigureBlobServiceClient(Uri serviceUri)
        {
            if (serviceUri is null) throw new ArgumentNullException(nameof(serviceUri));
            CreateClient = () => Task.FromResult(new BlobServiceClient(serviceUri, ClientOptions));
        }

        /// <summary>
        /// Configures the <see cref="BlobServiceClient"/> using the provided callback.
        /// </summary>
        public void ConfigureBlobServiceClient(Func<Task<BlobServiceClient>> createClientCallback)
        {
            CreateClient = createClientCallback ?? throw new ArgumentNullException(nameof(createClientCallback));
        }

        /// <summary>
        /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI and a <see cref="Azure.Core.TokenCredential"/>.
        /// </summary>
        public void ConfigureBlobServiceClient(Uri serviceUri, TokenCredential tokenCredential)
        {
            if (serviceUri is null) throw new ArgumentNullException(nameof(serviceUri));
            if (tokenCredential is null) throw new ArgumentNullException(nameof(tokenCredential));
            CreateClient = () => Task.FromResult(new BlobServiceClient(serviceUri, tokenCredential, ClientOptions));
        }

        /// <summary>
        /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI and a <see cref="Azure.AzureSasCredential"/>.
        /// </summary>
        public void ConfigureBlobServiceClient(Uri serviceUri, AzureSasCredential azureSasCredential)
        {
            if (serviceUri is null) throw new ArgumentNullException(nameof(serviceUri));
            if (azureSasCredential is null) throw new ArgumentNullException(nameof(azureSasCredential));
            CreateClient = () => Task.FromResult(new BlobServiceClient(serviceUri, azureSasCredential, ClientOptions));
        }

        /// <summary>
        /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI and a <see cref="StorageSharedKeyCredential"/>.
        /// </summary>
        public void ConfigureBlobServiceClient(Uri serviceUri, StorageSharedKeyCredential sharedKeyCredential)
        {
            if (serviceUri is null) throw new ArgumentNullException(nameof(serviceUri));
            if (sharedKeyCredential is null) throw new ArgumentNullException(nameof(sharedKeyCredential));
            CreateClient = () => Task.FromResult(new BlobServiceClient(serviceUri, sharedKeyCredential, ClientOptions));
        }
    }

    /// <summary>
    /// Configuration validator for AzureBlobStorageOptions
    /// </summary>
    public class AzureBlobStorageOptionsValidator : IConfigurationValidator
    {
        private readonly AzureBlobStorageOptions options;
        private readonly string name;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="options">The option to be validated.</param>
        /// <param name="name">The option name to be validated.</param>
        public AzureBlobStorageOptionsValidator(AzureBlobStorageOptions options, string name)
        {
            this.options = options;
            this.name = name;
        }

        public void ValidateConfiguration()
        {
            if (this.options.CreateClient is null)
            {
                throw new OrleansConfigurationException($"No credentials specified. Use the {options.GetType().Name}.{nameof(AzureBlobStorageOptions.ConfigureBlobServiceClient)} method to configure the Azure Blob Service client.");
            }

            try
            {
                AzureBlobUtils.ValidateContainerName(options.ContainerName);
                AzureBlobUtils.ValidateBlobName(this.name);
            }
            catch(ArgumentException e)
            {
                throw new OrleansConfigurationException(
                    $"Configuration for AzureBlobStorageOptions {name} is invalid. {nameof(this.options.ContainerName)} is not valid", e);
            }
        }
    }
}
