#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.AzureStorage
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.AzureStorage
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.AzureStorage
#elif ORLEANS_STREAMING
namespace Orleans.Streaming.AzureStorage
#elif ORLEANS_EVENTHUBS
namespace Orleans.Streaming.EventHubs
#elif TESTER_AZUREUTILS
namespace Orleans.Tests.AzureUtils
#elif ORLEANS_HOSTING_CLOUDSERVICES // Temporary until azure silo/client is refactored
namespace Orleans.Hosting.AzureCloudServices
#elif ORLEANS_TRANSACTIONS
namespace Orleans.Transactions.AzureStorage
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.AzureStorage
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    public abstract class AzureStorageOperationOptions
    {
        /// <summary>
        /// Azure Storage Policy Options
        /// </summary>
        public AzureStoragePolicyOptions StoragePolicyOptions { get; } = new AzureStoragePolicyOptions();
        
        /// <summary>
        /// Connection string for Azure Storage
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// Table name for Azure Storage
        /// </summary>
        public abstract string TableName { get; set; }
    }
}
