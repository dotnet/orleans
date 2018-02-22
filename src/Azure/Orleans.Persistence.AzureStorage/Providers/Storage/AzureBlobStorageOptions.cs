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

        #region json serialization
        public bool UseJson { get; set; }
        public bool UseFullAssemblyNames { get; set; }
        public bool IndentJson { get; set; }
        public TypeNameHandling? TypeNameHandling { get; set; }
        #endregion json serialization
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

    public class AzureBlobStorageOptionsFormatterResolver : IOptionFormatterResolver<AzureBlobStorageOptions>
    {
        private IOptionsSnapshot<AzureBlobStorageOptions> optionsSnapshot;

        public AzureBlobStorageOptionsFormatterResolver(IOptionsSnapshot<AzureBlobStorageOptions> optionsSnapshot)
        {
            this.optionsSnapshot = optionsSnapshot;
        }

        public IOptionFormatter<AzureBlobStorageOptions> Resolve(string name)
        {
            return new AzureBlobStorageOptionsFormatter(name, Options.Create(optionsSnapshot.Get(name)).Value);
        }

        private class AzureBlobStorageOptionsFormatter : IOptionFormatter<AzureBlobStorageOptions>
        {
            public string Name { get; }

            private AzureBlobStorageOptions options;

            public AzureBlobStorageOptionsFormatter(string name, AzureBlobStorageOptions options)
            {
                this.options = options;
                this.Name = OptionFormattingUtilities.Name<AzureBlobStorageOptions>(name);
            }

            public IEnumerable<string> Format()
            {
                return new List<string>()
                {
                    OptionFormattingUtilities.Format(nameof(this.options.ConnectionString), ConfigUtilities.RedactConnectionStringInfo(this.options.ConnectionString)),
                    OptionFormattingUtilities.Format(nameof(this.options.ContainerName),this.options.ContainerName),
                    OptionFormattingUtilities.Format(nameof(this.options.InitStage),this.options.InitStage),
                    OptionFormattingUtilities.Format(nameof(this.options.UseJson),this.options.UseJson),
                    OptionFormattingUtilities.Format(nameof(this.options.UseFullAssemblyNames),this.options.UseFullAssemblyNames),
                    OptionFormattingUtilities.Format(nameof(this.options.IndentJson),this.options.IndentJson),
                    OptionFormattingUtilities.Format(nameof(this.options.TypeNameHandling),this.options.TypeNameHandling),
                };
            }
        }
    }
}