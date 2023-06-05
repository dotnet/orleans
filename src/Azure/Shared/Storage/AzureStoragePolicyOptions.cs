using System;

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
#elif ORLEANS_TRANSACTIONS
namespace Orleans.Transactions.AzureStorage
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.AzureStorage
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    public class AzureStoragePolicyOptions
    {
        private TimeSpan? creationTimeout;
        private TimeSpan? operationTimeout;

        public int MaxBulkUpdateRows { get; set; } = 100;
        public int MaxCreationRetries { get; set; } = 60;
        public int MaxOperationRetries { get; set; } = 5;

        public TimeSpan PauseBetweenCreationRetries { get; set; } = TimeSpan.FromSeconds(1);

        public TimeSpan PauseBetweenOperationRetries { get; set; } = TimeSpan.FromMilliseconds(100);

        public TimeSpan CreationTimeout
        {
            get => creationTimeout ?? TimeSpan.FromMilliseconds(PauseBetweenCreationRetries.TotalMilliseconds * MaxCreationRetries * 3);
            set => SetIfValidTimeout(ref creationTimeout, value, nameof(CreationTimeout));
        }

        public TimeSpan OperationTimeout
        {
            get => operationTimeout ?? TimeSpan.FromMilliseconds(PauseBetweenOperationRetries.TotalMilliseconds * MaxOperationRetries * 6);
            set => SetIfValidTimeout(ref operationTimeout, value, nameof(OperationTimeout));
        }

        private static void SetIfValidTimeout(ref TimeSpan? field, TimeSpan value, string propertyName)
        {
            if (value > TimeSpan.Zero || value.Equals(TimeSpan.FromMilliseconds(-1)))
            {
                field = value;
            }
            else
            {
                throw new ArgumentNullException(propertyName);
            }
        }
    }
}
