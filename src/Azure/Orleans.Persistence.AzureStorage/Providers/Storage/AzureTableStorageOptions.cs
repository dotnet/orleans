using System;
using Newtonsoft.Json;
using Orleans.Persistence.AzureStorage;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration for AzureTableGrainStorage
    /// </summary>
    public class AzureTableStorageOptions : AzureStorageOperationOptions
    {
        /// <summary>
        /// Table name where grain stage is stored
        /// </summary>
        public override string TableName { get; set; } = DEFAULT_TABLE_NAME;
        public const string DEFAULT_TABLE_NAME = "OrleansGrainState";

        /// <summary>
        /// Indicates if grain data should be deleted or reset to defaults when a grain clears it's state.
        /// </summary>
        public bool DeleteStateOnClear { get; set; } = false;

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
    public class AzureTableGrainStorageOptionsValidator : AzureStorageOperationOptionsValidator<AzureTableStorageOptions>
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="options">The option to be validated.</param>
        /// <param name="name">The option name to be validated.</param>
        public AzureTableGrainStorageOptionsValidator(AzureTableStorageOptions options, string name) : base(options, name)
        {
        }
    }
}
