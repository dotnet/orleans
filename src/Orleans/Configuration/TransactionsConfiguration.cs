using System;

namespace Orleans.Transactions
{
    [Serializable]
    public class TransactionsConfiguration
    {
        public const string DefaultTableName = "OTXLog";

        /// <summary>
        /// The LogStorageType as string.
        /// </summary>
        public string LogStorageTypeName { get; set; }

        /// <summary>
        /// The LogStorageType value controls the persistent storage used for the transaction log. This value is resolved from the LogStorageTypeName attribute.
        /// </summary>
        public Type LogStorageType => ResolveType(LogStorageTypeName, nameof(LogStorageTypeName));

        /// <summary>
        /// The number of new Transaction Ids allocated on every write to the log.
        /// To avoid writing to log on every transaction start, transaction Ids are allocated in batches.
        /// </summary>
        public int TransactionIdAllocationBatchSize { get; set; }

        /// <summary>
        /// A new batch of transaction Ids will be automatically allocated if the available ids drop below
        /// this threshold.
        /// </summary>
        public int AvailableTransactionIdThreshold { get; set; }

        /// <summary>
        /// How long to preserve a transaction record in the TM memory after the transaction has completed.
        /// This is used to answer queries about the outcome of the transaction.
        /// </summary>
        public TimeSpan TransactionRecordPreservationDuration { get; set; }

        /// <summary>
        /// Provides connection string for an external table based transaction log storage.
        /// </summary>
        public string LogConnectionString { get; set; }

        /// <summary>
        /// Provides name of the table for an external table based transaction log storage.
        /// </summary>
        public string LogTableName { get; set; } = DefaultTableName;

        /// <summary>
        /// TransactionsConfiguration constructor.
        /// </summary>
        public TransactionsConfiguration()
        {
            TransactionIdAllocationBatchSize = 50000;
            AvailableTransactionIdThreshold = 20000;
            TransactionRecordPreservationDuration = TimeSpan.FromMinutes(1);
        }

        private static Type ResolveType(string typeName, string configurationValueName)
        {
            Type resolvedType = null;

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                resolvedType = Type.GetType(typeName);

                if (resolvedType == null)
                {
                    throw new InvalidOperationException($"Cannot locate the type specified in the configuration file for {configurationValueName}: '{typeName}'.");
                }
            }

            return resolvedType;
        }
    }
}
