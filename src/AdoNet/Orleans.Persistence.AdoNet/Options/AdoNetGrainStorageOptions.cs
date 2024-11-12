using Microsoft.Extensions.Options;
using Orleans.Persistence.AdoNet.Storage;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for ADO.NET grain storage.
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

        /// <summary>
        /// Gets or sets the hasher picker to use for this storage provider. 
        /// </summary>
        public IStorageHasherPicker HashPicker { get; set; }

        /// <summary>
        /// Sets legacy Orleans v3-compatible hash picker to use for this storage provider. Invoke this method if you need to run
        /// Orleans v7+ silo against existing Orleans v3-initialized database and keep existing grain state.
        /// </summary>
        public void UseOrleans3CompatibleHasher()
        {
            // content-aware hashing with different pickers, unable to use standard StorageHasherPicker
            this.HashPicker = new Orleans3CompatibleStorageHashPicker();
        }
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
            this.options = configurationOptions ?? throw new OrleansConfigurationException($"Invalid AdoNetGrainStorageOptions for AdoNetGrainStorage {name}. Options is required.");
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

            if (this.options.HashPicker == null)
            {
                throw new OrleansConfigurationException($"Invalid {nameof(AdoNetGrainStorageOptions)} values for {nameof(AdoNetGrainStorage)} {name}. {nameof(options.HashPicker)} is required.");
            }
        }
    }

    /// <summary>
    /// Provides default configuration HashPicker for AdoNetGrainStorageOptions.
    /// </summary>
    public class DefaultAdoNetGrainStorageOptionsHashPickerConfigurator : IPostConfigureOptions<AdoNetGrainStorageOptions>
    {
        public void PostConfigure(string name, AdoNetGrainStorageOptions options)
        {
            // preserving explicitly configured HashPicker
            if (options.HashPicker != null)
                return;

            // set default IHashPicker if not configured yet
            options.HashPicker = new StorageHasherPicker(new[] { new OrleansDefaultHasher() });
        }
    }
}
