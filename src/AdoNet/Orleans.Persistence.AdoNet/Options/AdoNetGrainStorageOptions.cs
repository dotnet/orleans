using System;
using Newtonsoft.Json;
using Orleans.Persistence.AdoNet.Storage;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for AdonetGrainStorage
    /// </summary>
    public class AdoNetGrainStorageOptions : IStorageProviderSerializerOptions
    {
        /// <summary>
        /// Connection string for AdoNet storage.
        /// </summary>
        [Redact]
        public string ConnectionString { get; set; }

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        /// <summary>
        /// Default init stage in silo lifecycle.
        /// </summary>
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        /// <summary>
        /// The default ADO.NET invariant used for storage if none is given. 
        /// </summary>
        public const string DEFAULT_ADONET_INVARIANT = AdoNetInvariants.InvariantNameSqlServer;
        /// <summary>
        /// The invariant name for storage.
        /// </summary>
        public string Invariant { get; set; } = DEFAULT_ADONET_INVARIANT;

        /// <inheritdoc/>
        public IGrainStorageSerializer GrainStorageSerializer { get; set; }
    }

    /// <summary>
    /// ConfigurationValidator for AdoNetGrainStorageOptions
    /// </summary>
    public class AdoNetGrainStorageOptionsValidator : IConfigurationValidator
    {
        private readonly AdoNetGrainStorageOptions options;
        private readonly string name;
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="configurationOptions">The option to be validated.</param>
        /// <param name="name">The name of the option to be validated.</param>
        public AdoNetGrainStorageOptionsValidator(AdoNetGrainStorageOptions configurationOptions, string name)
        {
            if(configurationOptions == null)
                throw new OrleansConfigurationException($"Invalid AdoNetGrainStorageOptions for AdoNetGrainStorage {name}. Options is required.");
            this.options = configurationOptions;
            this.name = name;
        }
        /// <inheritdoc cref="IConfigurationValidator"/>
        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(this.options.Invariant))
            {
                throw new OrleansConfigurationException($"Invalid {nameof(AdoNetGrainStorageOptions)} values for {nameof(AdoNetGrainStorage)} \"{name}\". {nameof(options.Invariant)} is required.");
            }

            if (string.IsNullOrWhiteSpace(this.options.ConnectionString))
            {
                throw new OrleansConfigurationException($"Invalid {nameof(AdoNetGrainStorageOptions)} values for {nameof(AdoNetGrainStorage)} \"{name}\". {nameof(options.ConnectionString)} is required.");
            }
        }
    }
}
