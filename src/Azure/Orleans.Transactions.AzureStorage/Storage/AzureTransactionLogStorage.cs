using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.WindowsAzure.Storage.RetryPolicies;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AzureStorage.Storage.Development;

namespace Orleans.Transactions.AzureStorage
{
    /// <summary>
    /// TransactionLog ported from research.  Placeholder, is being rewritten.
    /// </summary>
    public class AzureTransactionLogStorage : ITransactionLogStorage
    {
        private const string RowKey = "RowKey";
        private const string PartitionKey = "PartitionKey";

        private const int BatchOperationLimit = 100;
        private const int CommitRecordsPerRow = 10;

        private const string StartRowPartitionKey = "1";
        private const string StartRowRowKey = "0";

        //TODO: jbragg - Do not use serializationManager for persistent data!!
        private readonly SerializationManager serializationManager;
        private readonly AzureTransactionLogOptions options;

        // Azure Tables objects for persistent storage
        private CloudTable table;

        private long startRecordValue;
        private long nextLogSequenceNumber;
        private readonly string CommitRecordPartitionKey;

        // Log iteration indexes, reused between operations
        private TableContinuationToken currentContinuationToken;
        private TableQuerySegment<CommitRow> currentQueryResult;
        private int currentQueryResultIndex;
        private List<CommitRecord> currentRowTransactions;
        private int currentRowTransactionsIndex;
        private readonly ClusterOptions clusterOptions;
        private readonly AzureTransactionArchiveLogOptions archiveLogOptions;
        public AzureTransactionLogStorage(SerializationManager serializationManager, IOptions<AzureTransactionLogOptions> configurationOptions, 
            IOptions<AzureTransactionArchiveLogOptions> archiveOptions, IOptions<ClusterOptions> clusterOptions)
        {
            this.serializationManager = serializationManager;
            this.options = configurationOptions.Value;
            this.clusterOptions = clusterOptions.Value;
            this.archiveLogOptions = archiveOptions.Value;
            this.CommitRecordPartitionKey = this.clusterOptions.ServiceId;
        }

        public async Task Initialize()
        {
            if (string.IsNullOrWhiteSpace(this.options.ConnectionString))
            {
                throw new ArgumentNullException(nameof(this.options.ConnectionString));
            }

            // Retrieve the storage account from the connection string.
            var storageAccount = CloudStorageAccount.Parse(this.options.ConnectionString);

            // Create the table if not exists.
            CloudTableClient creationClient = storageAccount.CreateCloudTableClient();
            // TODO - do not hard code DefaultRequestOptions in rewrite.
            creationClient.DefaultRequestOptions.RetryPolicy = new LinearRetry(TimeSpan.FromSeconds(1), 60);
            creationClient.DefaultRequestOptions.ServerTimeout = TimeSpan.FromMinutes(3);
            creationClient.DefaultRequestOptions.PayloadFormat = TablePayloadFormat.JsonNoMetadata;
            CloudTable creationTable = creationClient.GetTableReference(this.options.TableName);
            await creationTable.CreateIfNotExistsAsync().ConfigureAwait(false);

            // get table for operations
            CloudTableClient operationClient = storageAccount.CreateCloudTableClient();
            // TODO - do not hard code DefaultRequestOptions in rewrite.
            operationClient.DefaultRequestOptions.RetryPolicy = new LinearRetry(TimeSpan.FromMilliseconds(100), 5);
            operationClient.DefaultRequestOptions.ServerTimeout = TimeSpan.FromSeconds(3);
            operationClient.DefaultRequestOptions.PayloadFormat = TablePayloadFormat.JsonNoMetadata;
            this.table = operationClient.GetTableReference(this.options.TableName);
            
            var query = new TableQuery<StartRow>().Where(TableQuery.CombineFilters(
                TableQuery.GenerateFilterCondition(PartitionKey, QueryComparisons.Equal, StartRowPartitionKey),
                TableOperators.And,
                TableQuery.GenerateFilterCondition(RowKey, QueryComparisons.LessThanOrEqual, StartRowRowKey)));
            var queryResult = await table.ExecuteQuerySegmentedAsync(query, null).ConfigureAwait(false);

            if (queryResult.Results.Count == 0)
            {
                // This is a fresh deployment, the StartRecord isn't created yet.
                // Create it here.
                var row = new StartRow(0);
                var operation = TableOperation.Insert(row);

                await table.ExecuteAsync(operation).ConfigureAwait(false);

                startRecordValue = 0;
            }
            else
            {
                startRecordValue = queryResult.Results[0].AllocatedTransactionIds;
            }
        }

        public async Task<CommitRecord> GetFirstCommitRecord()
        {
            currentContinuationToken = null;

            await ReadRowsFromTable(0);

            if (currentQueryResult.Results.Count == 0)
            {
                // The log has no log entries
                currentQueryResult = null;

                nextLogSequenceNumber = 1;

                return null;
            }

            currentRowTransactions = DeserializeCommitRecords(currentQueryResult.Results[0].Transactions);

            // TODO: Assert not empty?

            nextLogSequenceNumber = currentRowTransactions[currentRowTransactionsIndex].LSN + 1;

            return currentRowTransactions[currentRowTransactionsIndex++];
        }

        public async Task<CommitRecord> GetNextCommitRecord()
        {
            // Based on the current implementation logic in TransactionManager, this must be not be null, since
            // GetFirstCommitRecord sets a query or if no start record, the TransactionManager exits its loop.
            if (currentQueryResult == null)
            {
                throw new InvalidOperationException("GetNextCommitRecord called but currentQueryResult is null.");
            }

            if (currentRowTransactionsIndex == currentRowTransactions.Count)
            {
                currentQueryResultIndex++;
                currentRowTransactionsIndex = 0;
                currentRowTransactions = null;
            }

            if (currentQueryResultIndex == currentQueryResult.Results.Count)
            {
                // No more rows in our current segment, retrieve the next segment from the Table.
                if (currentContinuationToken == null)
                {
                    currentQueryResult = null;
                    return null;
                }

                await ReadRowsFromTable(0);
            }

            if (currentRowTransactions == null)
            {
                // TODO: assert currentRowTransactionsIndex = 0?
                currentRowTransactions = DeserializeCommitRecords(currentQueryResult.Results[currentQueryResultIndex].Transactions);
            }

            var currentTransaction = currentRowTransactions[currentRowTransactionsIndex++];

            nextLogSequenceNumber = currentTransaction.LSN + 1;

            return currentTransaction;
        }

        public Task<long> GetStartRecord()
        {
            return Task.FromResult(startRecordValue);
        }

        public async Task UpdateStartRecord(long transactionId)
        {
            var tableOperation = TableOperation.Replace(new StartRow(transactionId));

            await table.ExecuteAsync(tableOperation).ConfigureAwait(false);

            startRecordValue = transactionId;
        }

        public async Task Append(IEnumerable<CommitRecord> transactions)
        {
            var batchOperation = new TableBatchOperation();

            // TODO modify this to be able to use IEnumerable, fixed size array for serialization in the size of CommitRecordsPerRow, list is temporary
            var transactionList = new List<CommitRecord>(transactions);

            for (int nextRecord = 0; nextRecord < transactionList.Count; nextRecord += CommitRecordsPerRow)
            {
                var recordCount = Math.Min(transactionList.Count - nextRecord, CommitRecordsPerRow);
                var transactionSegment = transactionList.GetRange(nextRecord, recordCount);
                var commitRow = new CommitRow(nextLogSequenceNumber, this.clusterOptions.ServiceId);

                foreach (var transaction in transactionSegment)
                {
                    transaction.LSN = nextLogSequenceNumber++;
                }

                commitRow.Transactions = SerializeCommitRecords(transactionSegment);

                batchOperation.Insert(commitRow);

                if (batchOperation.Count == BatchOperationLimit)
                {
                    await table.ExecuteBatchAsync(batchOperation).ConfigureAwait(false);

                    batchOperation = new TableBatchOperation();
                }
            }

            if (batchOperation.Count > 0)
            {
                await table.ExecuteBatchAsync(batchOperation).ConfigureAwait(false);
            }
        }

        public async Task<List<CommitRecord>> QueryArchivalRecords(TableQuery<ArchivalRow> query)
        {
            var continuationToken = default(TableContinuationToken);
            var deserializedResults = new List<CommitRecord>();
            do
            {
                var queryResult = await table.ExecuteQuerySegmentedAsync(query, continuationToken).ConfigureAwait(false);
                continuationToken = queryResult.ContinuationToken;

                if (queryResult.Results.Count > 0)
                {
                    foreach (var row in queryResult)
                    {
                        var transactions = DeserializeCommitRecords(row.Transactions);
                        deserializedResults.AddRange(transactions);
                    }
                }
            } while (continuationToken != default(TableContinuationToken));

            return deserializedResults;
        }

        public async Task TruncateLog(long lsn)
        {
            var continuationToken = default(TableContinuationToken);
            var query = new TableQuery<CommitRow>().Where(TableQuery.CombineFilters(
                TableQuery.GenerateFilterCondition(PartitionKey, QueryComparisons.Equal, CommitRecordPartitionKey),
                TableOperators.And,
                TableQuery.GenerateFilterCondition(RowKey, QueryComparisons.LessThanOrEqual, CommitRow.ToRowKey(lsn))));
            var batchOperation = new TableBatchOperation();
            do
            {
                var queryResult = await table.ExecuteQuerySegmentedAsync(query, continuationToken).ConfigureAwait(false);

                continuationToken = queryResult.ContinuationToken;

                if (queryResult.Results.Count > 0)
                {

                    foreach (var row in queryResult)
                    {
                        var transactions = DeserializeCommitRecords(row.Transactions);

                        if (transactions.Count > 0 && transactions[transactions.Count - 1].LSN <= lsn)
                        {
                            batchOperation.Delete(row);
                            if (this.archiveLogOptions.ArchiveLog)
                            {
                                var archiveRow = new ArchivalRow(this.clusterOptions, row.Transactions, transactions.Select(tx => tx.TransactionId).Min(), transactions.Select(tx => tx.LSN).Min());
                                batchOperation.Insert(archiveRow);
                            }

                            if (batchOperation.Count == BatchOperationLimit)
                            {
                                await table.ExecuteBatchAsync(batchOperation).ConfigureAwait(false);
                                batchOperation = new TableBatchOperation();
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }

            } while (continuationToken != default(TableContinuationToken));

            if (batchOperation.Count > 0)
            {
                await table.ExecuteBatchAsync(batchOperation).ConfigureAwait(false);
            }
        }

        public static Factory<Task<ITransactionLogStorage>> Create(IServiceProvider serviceProvider)
        {
            return async () =>
            {
                AzureTransactionLogStorage storage = ActivatorUtilities.CreateInstance<AzureTransactionLogStorage>(serviceProvider, new object[0]);
                await storage.Initialize();
                return storage;
            };
        }

        private async Task ReadRowsFromTable(long keyLowerBound)
        {
            var query = new TableQuery<CommitRow>().Where(TableQuery.CombineFilters(
                TableQuery.GenerateFilterCondition(PartitionKey, QueryComparisons.Equal, CommitRecordPartitionKey),
                TableOperators.And,
                TableQuery.GenerateFilterCondition(RowKey, QueryComparisons.GreaterThanOrEqual, CommitRow.ToRowKey(keyLowerBound))));

            currentQueryResult = await table.ExecuteQuerySegmentedAsync(query, currentContinuationToken).ConfigureAwait(false);

            // Reset the indexes
            currentQueryResultIndex = 0;
            currentRowTransactionsIndex = 0;
            currentContinuationToken = currentQueryResult.ContinuationToken;
        }

        private byte[] SerializeCommitRecords(List<CommitRecord> commitRecords)
        {
            var serializableList = new List<Tuple<long, long, HashSet<ITransactionalResource>>>(commitRecords.Count);

            foreach (var commitRecord in commitRecords)
            {
                serializableList.Add(new Tuple<long, long, HashSet<ITransactionalResource>>(commitRecord.LSN, commitRecord.TransactionId, commitRecord.Resources));
            }

            var streamWriter = new BinaryTokenStreamWriter();

            serializationManager.Serialize(serializableList, streamWriter);

            return streamWriter.ToByteArray();
        }

        private List<CommitRecord> DeserializeCommitRecords(byte[] serializerCommitRecords)
        {
            if (serializerCommitRecords == null)
            {
                return new List<CommitRecord>();
            }

            var streamReader = new BinaryTokenStreamReader(serializerCommitRecords);

            var deserializedList = serializationManager.Deserialize<List<Tuple<long, long, HashSet<ITransactionalResource>>>>(streamReader);

            var commitRecords = new List<CommitRecord>(deserializedList.Count);

            foreach (var item in deserializedList)
            {
                commitRecords.Add(new CommitRecord { LSN = item.Item1, TransactionId = item.Item2, Resources = item.Item3 });
            }

            return commitRecords;
        }

        public class ArchivalRow : TableEntity
        {
            public static string MinRowKey = MakeRowKey(long.MinValue);
            public static string MaxRowKey = MakeRowKey(long.MaxValue);

            public static string MakeRowKey(long firstTransactionId)
            {
                return $"arch_{CommitRow.ToRowKey(firstTransactionId)}";
            }

            public ArchivalRow()
            {
            }

            public ArchivalRow(ClusterOptions clusterOptions, byte[] transactions, long firstTransactionId, long firstLSN)
            {
                this.Transactions = transactions;
                this.ClusterId = clusterOptions.ClusterId;
                this.FirstLSN = firstLSN;
                this.FirstTransactionId = firstTransactionId;
                PartitionKey = clusterOptions.ServiceId;
                RowKey = MakeRowKey(firstTransactionId);
            }

            public string ClusterId { get; set; }
            public long FirstLSN { get; set; }
            public long FirstTransactionId { get; set; }
            public byte[] Transactions { get; set; }
        }

        private class CommitRow : TableEntity
        {
            public CommitRow(long firstLSN, string serviceId)
            {
                // All entities are in the same partition for atomic read/writes.
                PartitionKey = serviceId;
                RowKey = ToRowKey(firstLSN);
            }

            public CommitRow()
            {
            }

            public byte[] Transactions { get; set; }

            internal static string ToRowKey(long lsn)
            {
                return $"{lsn:x16}";
            }
        }

        private class StartRow : TableEntity
        {
            public StartRow(long transactionId)
            {
                // only row in the table with this partition key
                PartitionKey = StartRowPartitionKey;
                RowKey = StartRowRowKey;
                ETag = "*";
                AllocatedTransactionIds = transactionId;
            }

            public StartRow()
            {
            }

            public long AllocatedTransactionIds { get; set; }
        }
    }
}
