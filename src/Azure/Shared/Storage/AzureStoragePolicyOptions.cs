using System;
using System.Threading;

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
    /// <summary>
    /// Configures batching, retry, and timeout behavior for Azure Table Storage operations.
    /// </summary>
    public class AzureStoragePolicyOptions
    {
        private TimeSpan? creationTimeout;
        private TimeSpan? operationTimeout;
        private TimeSpan? maxPauseBetweenOperationRetries;

        /// <summary>
        /// Gets or sets the maximum number of entities included in a bulk update or delete operation.
        /// </summary>
        public int MaxBulkUpdateRows { get; set; } = 100;

        /// <summary>
        /// Gets or sets the maximum number of attempts to create or connect to the table during initialization.
        /// </summary>
        public int MaxCreationRetries { get; set; } = 60;

        // Defaults match Azure Storage SDK retry settings.
        /// <summary>
        /// The maximum number of operation retry attempts.
        /// </summary>
        public int MaxOperationRetries { get; set; } = 5;

        /// <summary>
        /// Gets or sets the delay between attempts to create or connect to the table.
        /// </summary>
        public TimeSpan PauseBetweenCreationRetries { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// The base delay used by the Azure SDK exponential retry policy.
        /// </summary>
        public TimeSpan PauseBetweenOperationRetries { get; set; } = TimeSpan.FromSeconds(0.8);

        /// <summary>
        /// The maximum delay used by the Azure SDK exponential retry policy.
        /// </summary>
        public TimeSpan MaxPauseBetweenOperationRetries
        {
            get => this.maxPauseBetweenOperationRetries ?? TimeSpan.FromMinutes(1);
            set
            {
                if (value <= TimeSpan.Zero && !value.Equals(Timeout.InfiniteTimeSpan))
                {
                    throw new ArgumentOutOfRangeException(nameof(MaxPauseBetweenOperationRetries), value, "Value must be positive or Timeout.InfiniteTimeSpan.");
                }
                this.maxPauseBetweenOperationRetries = value;
            }
        }

        /// <summary>
        /// Gets or sets the timeout for creating or connecting to the table.
        /// </summary>
        /// <remarks>
        /// The default is three times the product of <see cref="PauseBetweenCreationRetries"/> and <see cref="MaxCreationRetries"/>.
        /// </remarks>
        public TimeSpan CreationTimeout
        {
            get => this.creationTimeout ?? TimeSpan.FromMilliseconds(this.PauseBetweenCreationRetries.TotalMilliseconds * this.MaxCreationRetries * 3);
            set => SetIfValidTimeout(ref this.creationTimeout, value, nameof(CreationTimeout));
        }

        /// <summary>
        /// Gets or sets the duration after which a table operation is reported as slow.
        /// </summary>
        public TimeSpan OperationTimeout
        {
            get => this.operationTimeout ?? TimeSpan.FromSeconds(100);
            set => SetIfValidTimeout(ref this.operationTimeout, value, nameof(OperationTimeout));
        }

        private static void SetIfValidTimeout(ref TimeSpan? field, TimeSpan value, string propertyName)
        {
            if (value > TimeSpan.Zero || value.Equals(Timeout.InfiniteTimeSpan))
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
