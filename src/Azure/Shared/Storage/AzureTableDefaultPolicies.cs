using System;
using System.Diagnostics;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using Orleans.Runtime;

//
// Number of #ifs can be reduced (or removed), once we separate test projects by feature/area, otherwise we are ending up with ambigous types and build errors.
//

#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.AzureStorage
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.AzureStorage
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.AzureStorage
#elif ORLEANS_STATISTICS
namespace Orleans.Statistics.AzureStorage
#elif ORLEANS_STREAMING
namespace Orleans.Streaming.AzureStorage
#elif ORLEANS_EVENTHUBS
namespace Orleans.Streaming.EventHubs
#elif TESTER_AZUREUTILS
namespace Orleans.Tests.AzureUtils
#elif ORLEANS_HOSTING_CLOUDSERVICES // Temporary until azure silo/client is refactored
namespace Orleans.Hosting.AzureCloudServices
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    /// <summary>
    /// Utility class for default retry / timeout settings for Azure storage.
    /// </summary>
    /// <remarks>
    /// These functions are mostly intended for internal usage by Orleans runtime, but due to certain assembly packaging constrants this class needs to have public visibility.
    /// </remarks>
    internal static class AzureTableDefaultPolicies
    {
        public static int MaxTableCreationRetries { get; private set; }
        public static int MaxTableOperationRetries { get; private set; }
        public static int MaxBusyRetries { get; internal set; }

        public static TimeSpan PauseBetweenTableCreationRetries { get; private set; }
        public static TimeSpan PauseBetweenTableOperationRetries { get; private set; }
        public static TimeSpan PauseBetweenBusyRetries { get; private set; }

        public static TimeSpan TableCreationTimeout { get; private set; }
        public static TimeSpan TableOperationTimeout { get; private set; }
        public static TimeSpan BusyRetriesTimeout { get; private set; }

        public static IRetryPolicy TableCreationRetryPolicy { get; private set; }
        public static IRetryPolicy TableOperationRetryPolicy { get; private set; }

        public const int MAX_BULK_UPDATE_ROWS = 100;

        static AzureTableDefaultPolicies()
        {
            MaxTableCreationRetries = 60;
            PauseBetweenTableCreationRetries = TimeSpan.FromSeconds(1);

            MaxTableOperationRetries = 5;
            PauseBetweenTableOperationRetries = TimeSpan.FromMilliseconds(100);

            MaxBusyRetries = 120;
            PauseBetweenBusyRetries = TimeSpan.FromMilliseconds(500);

            if (Debugger.IsAttached)
            {
                PauseBetweenTableCreationRetries = PauseBetweenTableCreationRetries.Multiply(100);
                PauseBetweenTableOperationRetries = PauseBetweenTableOperationRetries.Multiply(100);
                PauseBetweenBusyRetries = PauseBetweenBusyRetries.Multiply(10);
            }

            TableCreationRetryPolicy = new LinearRetry(PauseBetweenTableCreationRetries, MaxTableCreationRetries); // 60 x 1s
            TableCreationTimeout = PauseBetweenTableCreationRetries.Multiply(MaxTableCreationRetries).Multiply(3);    // 3 min

            TableOperationRetryPolicy = new LinearRetry(PauseBetweenTableOperationRetries, MaxTableOperationRetries); // 5 x 100ms
            TableOperationTimeout = PauseBetweenTableOperationRetries.Multiply(MaxTableOperationRetries).Multiply(6);    // 3 sec

            BusyRetriesTimeout = PauseBetweenBusyRetries.Multiply(MaxBusyRetries);  // 1 minute
        }
    }
}
