using System;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json;
using Orleans.Persistence.AzureStorage;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

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
        /// Container name where grain stage is stored
        /// </summary>
        public string ContainerName { get; set; } = DEFAULT_CONTAINER_NAME;
        public const string DEFAULT_CONTAINER_NAME = "grainstate";

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialzed prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        public bool UseJson { get; set; }
        public bool UseFullAssemblyNames { get; set; }
        public bool IndentJson { get; set; }
        public TypeNameHandling? TypeNameHandling { get; set; }
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
            if (!CloudStorageAccount.TryParse(this.options.ConnectionString, out var ignore))
                throw new OrleansConfigurationException(
                    $"Configuration for AzureBlobStorageOptions {name} is invalid. {nameof(this.options.ConnectionString)} is not valid.");
            try
            {
                AzureStorageUtils.ValidateContainerName(options.ContainerName);
            }
            catch(ArgumentException e)
            {
                throw new OrleansConfigurationException(
                    $"Configuration for AzureBlobStorageOptions {name} is invalid. {nameof(this.options.ContainerName)} is not valid", e);
            }
        }
    }
}