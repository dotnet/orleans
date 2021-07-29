using System;
using Newtonsoft.Json;
using Orleans.AzureCosmos;

namespace Orleans.Configuration
{
    /// <summary>
    /// Specify options used for Azure Cosmos DB grain storage
    /// </summary>
    public class AzureCosmosStorageOptions : StorageOptionsBase
    {
        public AzureCosmosStorageOptions() => ContainerName = DEFAULT_CONTAINER_NAME;
        public const string DEFAULT_CONTAINER_NAME = "OrleansGrainState";

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        /// <summary>
        /// If true, state updates will unconditionally overwrite/delete existing values in DB when ETag conflicts are detected
        /// </summary>
        /// <remarks>https://github.com/dotnet/orleans/issues/5110</remarks>
        public bool OverwriteStateOnUpdateConflict { get; set; } = false;

        public Action<JsonSerializerSettings> ConfigureJsonSerializerSettings { get; set; }
    }
}
