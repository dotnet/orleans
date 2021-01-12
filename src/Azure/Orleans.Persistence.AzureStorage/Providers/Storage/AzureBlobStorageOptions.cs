using System;
using Azure.Core;
using Microsoft.Azure.Cosmos.Table;
using Newtonsoft.Json;
using Orleans.Persistence.AzureStorage;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    public class AzureBlobStorageOptions
    {
        /// <summary>
        /// Azure connection string
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// The Service URI (e.g. https://x.blob.core.windows.net). Required for specifying <see cref="TokenCredential"/>.
        /// </summary>
        public Uri ServiceUri { get; set; }

        /// <summary>
        /// Use AAD to access the storage account
        /// </summary>
        public TokenCredential TokenCredential { get; set; }

        /// <summary>
        /// Container name where grain stage is stored
        /// </summary>
        public string ContainerName { get; set; } = DEFAULT_CONTAINER_NAME;
        public const string DEFAULT_CONTAINER_NAME = "grainstate";

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
    /// Configuration validator for AzureTableStorageOptions
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

                if (!CloudStorageAccount.TryParse(this.options.ConnectionString, out _))
                    throw new OrleansConfigurationException(
                        $"Configuration for AzureBlobStorageOptions {name} is invalid. {nameof(this.options.ConnectionString)} is not valid.");
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
