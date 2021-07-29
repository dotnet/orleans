using System;

namespace Orleans.AzureCosmos
{
    public abstract class StorageOptionsBase
    {
        /// <summary>
        /// Connection string for Azure Cosmos DB
        /// </summary>
        //[RedactConnectionString]
        //public string ConnectionString { get; set; }

        // Temporary shortcut for faster iteration
        [Redact]
        [field: NonSerialized]
        public Func<Microsoft.Azure.Cosmos.CosmosClient> Connection { get; set; }

        /// <summary>
        /// Database name for Azure Cosmos DB
        /// </summary>
        public string DatabaseName { get; set; }

        /// <summary>
        /// Container name for Azure Cosmos DB
        /// </summary>
        public string ContainerName { get; set; }
    }
}
