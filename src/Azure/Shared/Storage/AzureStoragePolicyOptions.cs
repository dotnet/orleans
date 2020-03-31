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
        public int MAX_BULK_UPDATE_ROWS { get; set; }
        public int MaxCreationRetries { get; set; }
        public int MaxOperationRetries { get; set; }
        public int MaxBusyRetries { get; set; }

        public TimeSpan PauseBetweenCreationRetries { get; set; }

        public TimeSpan PauseBetweenOperationRetries { get; set; }

        public TimeSpan PauseBetweenBusyRetries { get; set; }

        public TimeSpan CreationTimeout { get; set; }
        public TimeSpan OperationTimeout { get; set; }
        public TimeSpan BusyRetriesTimeout { get; set; }

        public IRetryPolicy CreationRetryPolicy { get; set; }
        public IRetryPolicy OperationRetryPolicy { get; set; }
        
        public AzureStoragePolicyOptions()
        {
        	this.MAX_BULK_UPDATE_ROWS = 100;
            this.MaxCreationRetries = 60;
			this.MaxOperationRetries = 5;
			this.MaxBusyRetries = 120;

			this.PauseBetweenCreationRetries = (!Debugger.IsAttached)
					? TimeSpan.FromSeconds(1)
					: TimeSpan.FromSeconds(100);

			this.PauseBetweenOperationRetries = (!Debugger.IsAttached)
					? TimeSpan.FromMilliseconds(100)
					: TimeSpan.FromSeconds(10);

			this.PauseBetweenBusyRetries = (!Debugger.IsAttached)
					? TimeSpan.FromMilliseconds(500)
					: TimeSpan.FromSeconds(5);

			this.CreationTimeout = TimeSpan.FromMilliseconds(this.PauseBetweenCreationRetries.TotalMilliseconds * this.MaxCreationRetries * 3);
			this.OperationTimeout = TimeSpan.FromMilliseconds(this.PauseBetweenOperationRetries.TotalMilliseconds * this.MaxOperationRetries * 6);
			this.BusyRetriesTimeout = TimeSpan.FromMilliseconds(this.PauseBetweenBusyRetries.TotalMilliseconds * this.MaxBusyRetries);
			this.CreationRetryPolicy = new LinearRetry(this.PauseBetweenCreationRetries, this.MaxCreationRetries);
			this.OperationRetryPolicy = new LinearRetry(this.PauseBetweenOperationRetries, this.MaxOperationRetries);    
        }
    
    }
}
