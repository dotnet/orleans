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
    public class AzureBlobStorageOptions : AzureBlobUtils.IBlobServiceClientOptions
    {
        /// <summary>
        /// Container name where grain stage is stored
        /// </summary>
        public string ContainerName { get; set; } = DEFAULT_CONTAINER_NAME;
        public const string DEFAULT_CONTAINER_NAME = "grainstate";

        /// <summary>
        /// The service connection string.
        /// </summary>
        /// <remarks>
        /// This property is superseded by all other properties except for <see cref="ServiceUri"/>.
        /// </remarks>
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// The blob service endpoint (e.g. https://x.blob.core.windows.net).
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
        /// Options to be used when configuring the blob storage client, or <see langword="null"/> to use the default options.
        /// </summary>
        public BlobClientOptions ClientOptions { get; set; }

        /// <summary>
        /// Shared key credentials, to be used in conjunction with <see cref="ServiceUri"/>.
        /// </summary>
        /// <remarks>
        /// This property takes precedence over specifying only <see cref="ServiceUri"/> and over <see cref="ConnectionString"/>.
        /// This property is superseded by <see cref="CreateClient"/>, <see cref="TokenCredential"/>, and <see cref="AzureSasCredential"/>.
        /// </remarks>
        public StorageSharedKeyCredential SharedKeyCredential { get; set; }

        /// <summary>
        /// The optional delegate used to create a <see cref="BlobServiceClient"/> instance.
        /// </summary>
        /// <remarks>
        /// This property, if not <see langword="null"/>, takes precedence over <see cref="ConnectionString"/>, <see cref="SharedKeyCredential"/>, <see cref="AzureSasCredential"/>, <see cref="TokenCredential"/>, <see cref="ClientOptions"/>, and <see cref="ServiceUri"/>,
        /// </remarks>
        public Func<Task<BlobServiceClient>> CreateClient { get; set; }

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
            if (this.options.ServiceUri == null)
            {
                if (this.options.TokenCredential != null)
                {
                    throw new OrleansConfigurationException($"Configuration for AzureBlobStorageOptions {name} is invalid. {nameof(options.ServiceUri)} is required for {nameof(options.TokenCredential)}");
                }
            }
            else
            {
                if (this.options.TokenCredential == null)
                    throw new OrleansConfigurationException(
                        $"Configuration for AzureBlobStorageOptions {name} is invalid. {nameof(this.options.TokenCredential)} is missing.");
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
