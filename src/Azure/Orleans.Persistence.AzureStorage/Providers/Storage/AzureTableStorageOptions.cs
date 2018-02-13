using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Configuration for AzureTableGrainStorage
    /// </summary>
    public class AzureTableStorageOptions
    {
        /// <summary>
        /// Gets or sets a unique identifier for this service, which should survive deployment and redeployment.
        /// </summary>
        public string ServiceId { get; set; } = string.Empty;

        /// <summary>
        /// Azure table connection string
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Table name where grain stage is stored
        /// </summary>
        public string TableName { get; set; } = DEFAULT_TABLE_NAME;
        public const string DEFAULT_TABLE_NAME = "OrleansGrainState";

        /// <summary>
        /// Indicates if grain data should be deleted or reset to defaults when a grain clears it's state.
        /// </summary>
        public bool DeleteStateOnClear { get; set; } = false;

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialzed prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = SiloLifecycleStage.ApplicationServices;

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
    public class AzureTableGrainStorageOptionsValidator : IConfigurationValidator
    {
        private readonly AzureTableStorageOptions options;
        private readonly string name;
        public AzureTableGrainStorageOptionsValidator(AzureTableStorageOptions options, string name)
        {
            this.options = options;
            this.name = name;
        }

        public void ValidateConfiguration()
        {
            if (string.IsNullOrEmpty(this.options.ConnectionString))
                throw new OrleansConfigurationException(
                    $"Configuration for AzureTableStorageProvider {name} is invalid. Connection is required.");
        }
    }


    public class AzureTableStorageOptionsFormatterResolver : IOptionFormatterResolver<AzureTableStorageOptions>
    {
        private IOptionsSnapshot<AzureTableStorageOptions> optionsSnapshot;
        public AzureTableStorageOptionsFormatterResolver(IOptionsSnapshot<AzureTableStorageOptions> optionsSnapshot)
        {
            this.optionsSnapshot = optionsSnapshot;
        }

        public IOptionFormatter<AzureTableStorageOptions> Resolve(string name)
        {
            return new AzureTableStorageOptionsFormatter(name, Options.Create(optionsSnapshot.Get(name)).Value);
        }

        private class AzureTableStorageOptionsFormatter : IOptionFormatter<AzureTableStorageOptions>
        {
            public string Name { get; }

            private AzureTableStorageOptions options;
            public AzureTableStorageOptionsFormatter(string name,  AzureTableStorageOptions options)
            {
                this.options = options;
                this.Name = OptionFormattingUtilities.Name<AzureTableStorageOptions>(name);
            }

            public IEnumerable<string> Format()
            {
                return new List<string>()
                {
                    OptionFormattingUtilities.Format(nameof(this.options.ServiceId),this.options.ServiceId),
                    OptionFormattingUtilities.Format(nameof(this.options.ConnectionString), ConfigUtilities.RedactConnectionStringInfo(this.options.ConnectionString)),
                    OptionFormattingUtilities.Format(nameof(this.options.TableName),this.options.TableName),
                    OptionFormattingUtilities.Format(nameof(this.options.DeleteStateOnClear),this.options.DeleteStateOnClear),
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
