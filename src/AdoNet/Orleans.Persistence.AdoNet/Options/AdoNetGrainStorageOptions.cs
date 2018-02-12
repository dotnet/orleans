using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Persistence.AdoNet.Storage;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans.Hosting
{
    public class AdoNetGrainStorageOptions
    {
        /// <summary>
        /// Connection string for AdoNet storage
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialzed prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        /// <summary>
        /// Default init stage in silo lifecycle
        /// </summary>
        public const int DEFAULT_INIT_STAGE = SiloLifecycleStage.ApplicationServices;

        /// <summary>
        /// The default ADO.NET invariant used for storage if none is given. 
        /// </summary>
        public const string DEFAULT_ADONET_INVARIANTS = AdoNetInvariants.InvariantNameSqlServer;
        /// <summary>
        /// The invariant name for storage
        /// </summary>
        public string AdoInvariant { get; set; } = DEFAULT_ADONET_INVARIANTS;

        #region json serialization related settings
        /// <summary>
        /// Whether storage string payload should be formatted in JSON.
        /// </summary>
        public bool UseJsonFormat { get; set; }
        public bool UseFullAssemblyNames { get; set; }
        public bool IndentJson { get; set; }
        public TypeNameHandling? TypeNameHandling { get; set; }
        #endregion
        /// <summary>
        /// Whether storage string payload should be formatted in Xml.
        /// </summary>
        public bool UseXmlFormat { get; set; }
    }
    public class AdoNetGrainStorageOptionsValidator : IConfigurationValidator
    {
        private readonly AdoNetGrainStorageOptions options;
        private readonly string name;
        public AdoNetGrainStorageOptionsValidator(AdoNetGrainStorageOptions configurationOptions, string name)
        {
            this.options = configurationOptions;
            this.name = name;
        }

        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(this.options.ConnectionString))
            {
                throw new OrleansConfigurationException($"Invalid AdoNetGrainStorageOptions for AdoNetGrainStorage {name}. ConnectionString is required.");
            }
            if (options.UseXmlFormat&&options.UseJsonFormat)
            {
                throw new OrleansConfigurationException($"Invalid AdoNetGrainStorageOptions for AdoNetGrainStorage {name}. Only one serializer and deserializer should be given.");
            }
        }
    }

    public class AdoNetStorageOptionsFormatterResolver : IOptionFormatterResolver<AdoNetGrainStorageOptions>
    {
        private IOptionsSnapshot<AdoNetGrainStorageOptions> optionsSnapshot;
        public AdoNetStorageOptionsFormatterResolver(IOptionsSnapshot<AdoNetGrainStorageOptions> optionsSnapshot)
        {
            this.optionsSnapshot = optionsSnapshot;
        }

        public IOptionFormatter<AdoNetGrainStorageOptions> Resolve(string name)
        {
            return new AzureTableStorageOptionsFormatter(name, optionsSnapshot.Get(name));
        }

        private class AzureTableStorageOptionsFormatter : IOptionFormatter<AdoNetGrainStorageOptions>
        {
            public string Name { get; }

            private AdoNetGrainStorageOptions options;
            public AzureTableStorageOptionsFormatter(string name, AdoNetGrainStorageOptions options)
            {
                this.options = options;
                this.Name = OptionFormattingUtilities.Name<AdoNetGrainStorageOptions>(name);
            }

            public IEnumerable<string> Format()
            {
                return new List<string>()
                {
                    OptionFormattingUtilities.Format(nameof(this.options.ConnectionString),this.options.ConnectionString),
                    OptionFormattingUtilities.Format(nameof(this.options.InitStage),this.options.InitStage),
                    OptionFormattingUtilities.Format(nameof(this.options.AdoInvariant),this.options.AdoInvariant),
                    OptionFormattingUtilities.Format(nameof(this.options.UseJsonFormat),this.options.UseJsonFormat),
                    OptionFormattingUtilities.Format(nameof(this.options.UseFullAssemblyNames),this.options.UseFullAssemblyNames),
                    OptionFormattingUtilities.Format(nameof(this.options.IndentJson),this.options.IndentJson),
                    OptionFormattingUtilities.Format(nameof(this.options.TypeNameHandling),this.options.TypeNameHandling),
                    OptionFormattingUtilities.Format(nameof(this.options.UseXmlFormat),this.options.UseXmlFormat),
                };
            }
        }
    }
}
