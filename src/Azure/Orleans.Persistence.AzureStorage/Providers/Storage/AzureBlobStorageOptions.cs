using System;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Persistence.AzureStorage;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Configuration
{
    public enum AzureBlobStorageWriteMode
    {
        /// <summary>
        /// Serialize to <see cref="BinaryData"/> and upload.
        /// This uses the <see cref="IGrainStorageSerializer"/> binary path, materializing the full payload in memory.
        /// It is typically the fastest path but can create large allocations (including LOH) for big payloads.
        /// </summary>
        BinaryData,

        /// <summary>
        /// Serialize using the stream serializer into a pooled in-memory stream and upload from that buffer.
        /// This still buffers the full payload but avoids LOH churn by reusing pooled segments.
        /// Requires <see cref="IGrainStorageStreamingSerializer"/>; otherwise the write falls back to <see cref="BinaryData"/>.
        /// </summary>
        BufferedStream,
    }

    public class AzureBlobStorageOptions : IStorageProviderSerializerOptions
    {
        private BlobServiceClient _blobServiceClient;

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

        /// <inheritdoc/>
        public IGrainStorageSerializer GrainStorageSerializer { get; set; }

        /// <summary>
        /// Gets or sets the client used to access the Azure Blob Service.
        /// </summary>
        public BlobServiceClient BlobServiceClient
        {
            get => _blobServiceClient; set
            {
                _blobServiceClient = value;
                CreateClient = () => Task.FromResult(value);
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether to delete the state when <see cref="IGrainStorage.ClearStateAsync"/> is called.  Defaults to true.
        /// </summary>
        public bool DeleteStateOnClear { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether to use pooled buffers when reading blob contents.
        /// The deserializer must not retain the <see cref="BinaryData"/> or underlying buffer after deserialization.
        /// When a stream serializer is configured, pooled buffers are used only if the content length fits in an <see cref="int"/>.
        /// When pooled buffers are used, deserialization goes through the <see cref="IGrainStorageSerializer"/> binary path.
        /// </summary>
        public bool UsePooledBufferForReads { get; set; } = true;

        /// <summary>
        /// Gets or sets the write path to use when a stream serializer is available.
        /// If the stream serializer is not configured, writes always use <see cref="BinaryData"/>.
        /// </summary>
        public AzureBlobStorageWriteMode WriteMode { get; set; } = AzureBlobStorageWriteMode.BinaryData;

        /// <summary>
        /// A function for building container factory instances
        /// </summary>
        public Func<IServiceProvider, AzureBlobStorageOptions, IBlobContainerFactory> BuildContainerFactory { get; set; }
            = static (provider, options) => ActivatorUtilities.CreateInstance<DefaultBlobContainerFactory>(provider, options);

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
            catch (ArgumentException e)
            {
                throw new OrleansConfigurationException(
                    $"Configuration for AzureBlobStorageOptions {name} is invalid. {nameof(this.options.ContainerName)} is not valid", e);
            }
        }
    }
}
