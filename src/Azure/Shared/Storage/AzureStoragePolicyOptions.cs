using System;
using System.Diagnostics;
using Microsoft.Azure.Cosmos.Table;

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
    public class AzureStoragePolicyOptions
    {
        private TimeSpan? creationTimeout;
        private TimeSpan? operationTimeout;
        private IRetryPolicy creationRetryPolicy;
        private IRetryPolicy operaionRetryPolicy;

        public int MaxBulkUpdateRows { get; set; } = 100;
        public int MaxCreationRetries { get; set; } = 60;
        public int MaxOperationRetries { get; set; } = 5;

        public TimeSpan PauseBetweenCreationRetries { get; set; } = TimeSpan.FromSeconds(1);

        public TimeSpan PauseBetweenOperationRetries { get; set; } = TimeSpan.FromMilliseconds(100);

        public TimeSpan CreationTimeout
        {
            get => this.creationTimeout ?? TimeSpan.FromMilliseconds(this.PauseBetweenCreationRetries.TotalMilliseconds * this.MaxCreationRetries * 3);
            set => SetIfValidTimeout(ref this.creationTimeout, value, nameof(CreationTimeout));
        }

        public TimeSpan OperationTimeout
        {
            get => this.operationTimeout ?? TimeSpan.FromMilliseconds(this.PauseBetweenOperationRetries.TotalMilliseconds * this.MaxOperationRetries * 6);
            set => SetIfValidTimeout(ref this.operationTimeout, value, nameof(OperationTimeout));
        }

        public IRetryPolicy CreationRetryPolicy
        {
            get => this.creationRetryPolicy ??= new LinearRetry(this.PauseBetweenCreationRetries, this.MaxCreationRetries);
            set => this.creationRetryPolicy = value ?? throw new ArgumentNullException(nameof(CreationRetryPolicy));
        }

        public IRetryPolicy OperationRetryPolicy
        {
            get => this.operaionRetryPolicy ??= new LinearRetry(this.PauseBetweenOperationRetries, this.MaxOperationRetries);
            set => this.operaionRetryPolicy = value ?? throw new ArgumentNullException(nameof(OperationRetryPolicy));
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
