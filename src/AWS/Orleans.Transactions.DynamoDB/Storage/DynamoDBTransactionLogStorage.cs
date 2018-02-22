using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Transactions.Abstractions;
using Microsoft.Extensions.Logging;
using Amazon.DynamoDBv2.Model;
using Amazon.DynamoDBv2;
using System.Globalization;
using System.IO;

namespace Orleans.Transactions.DynamoDB
{
    public class DynamoDBTransactionLogStorage : ITransactionLogStorage
    {

        private const string RowKey = "RowKey";
        private const string PartitionKey = "PartitionKey";
        private const string AllocatedTransactionIdsKey = "AllocatedTransactionIds";
        private const string TransactionsKey = "Transactions";
        private const string RowKeyAlias = ":RowKey";
        private const string PartitionKeyAlias = ":PartitionKey";

        private const int BatchOperationLimit = 25;
        private const int CommitRecordsPerRow = 40;
        private const string CommitRecordPartitionKey = "0";

        private const string StartRowPartitionKey = "1";
        private static readonly AttributeValue StartRowRowKey = new AttributeValue { N = "0" };

        //TODO: jbragg - Do not use serializationManager for persistent data!!
        private readonly SerializationManager serializationManager;
        private readonly DynamoDBTransactionLogOptions options;
        private readonly ILoggerFactory loggerFactory;

        private DynamoDBStorage storage;

        private long startRecordValue;
        private long nextLogSequenceNumber;

        // Log iteration indexes, reused between operations
        private Dictionary<string, AttributeValue> currentLastEvaluatedKey;
        private List<CommitRow> currentQueryResult;
        private int currentQueryResultIndex;
        private List<CommitRecord> currentRowTransactions;
        private int currentRowTransactionsIndex;

        public DynamoDBTransactionLogStorage(SerializationManager serializationManager, IOptions<DynamoDBTransactionLogOptions> configurationOptions, ILoggerFactory loggerFactory)
        {
            this.serializationManager = serializationManager;
            this.options = configurationOptions.Value;
            this.loggerFactory = loggerFactory;
        }

        public async Task Initialize()
        {
            storage = new DynamoDBStorage(this.loggerFactory, this.options.Service, this.options.AccessKey, this.options.SecretKey,
                this.options.ReadCapacityUnits, this.options.WriteCapacityUnits);
            await storage.InitializeTable(this.options.TableName,
                new List<KeySchemaElement>
                {
                    new KeySchemaElement { AttributeName = PartitionKey, KeyType = KeyType.HASH },
                    new KeySchemaElement { AttributeName = RowKey, KeyType = KeyType.RANGE }
                },
                new List<AttributeDefinition>
                {
                    new AttributeDefinition { AttributeName = PartitionKey, AttributeType = ScalarAttributeType.S },
                    new AttributeDefinition { AttributeName = RowKey, AttributeType = ScalarAttributeType.N }
                }).ConfigureAwait(false);

            var (results, lastEvaluatedKey) = await storage.QueryAsync(this.options.TableName,
                new Dictionary<string, AttributeValue>
                {
                    { PartitionKeyAlias, new AttributeValue(StartRowPartitionKey) },
                    { RowKeyAlias, StartRowRowKey }
                },
                $"{PartitionKey} = {PartitionKeyAlias} AND {RowKey} <= {RowKeyAlias}",
                (fields) =>
                {
                    return new StartRow(AttributeToLong(fields[AllocatedTransactionIdsKey]));
                }).ConfigureAwait(false);

            if (results.Count == 0)
            {
                // This is a fresh deployment, the StartRecord isn't created yet.
                // Create it here.
                await storage.PutEntryAsync(this.options.TableName,
                    new Dictionary<string, AttributeValue>
                    {
                        { PartitionKey, new AttributeValue(StartRowPartitionKey) },
                        { RowKey, StartRowRowKey },
                        { AllocatedTransactionIdsKey, new AttributeValue { N = "0" } }
                    }).ConfigureAwait(false);

                startRecordValue = 0;
            }
            else
            {
                startRecordValue = results[0].AllocatedTransactionIds;
            }
        }

        public static Factory<Task<ITransactionLogStorage>> Create(IServiceProvider serviceProvider)
        {
            return async () =>
            {
                DynamoDBTransactionLogStorage logStorage = ActivatorUtilities.CreateInstance<DynamoDBTransactionLogStorage>(serviceProvider, new object[0]);
                await logStorage.Initialize();
                return logStorage;
            };
        }

        public async Task<CommitRecord> GetFirstCommitRecord()
        {
            currentLastEvaluatedKey = null;

            await ReadRowsFromTable(0);

            if (currentQueryResult.Count == 0)
            {
                // The log has no log entries
                currentQueryResult = null;

                nextLogSequenceNumber = 1;

                return null;
            }

            currentRowTransactions = DeserializeCommitRecords(currentQueryResult[0].Transactions);

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

            if (currentQueryResultIndex == currentQueryResult.Count)
            {
                // No more rows in our current segment, retrieve the next segment from the Table.
                if (currentLastEvaluatedKey == null || currentLastEvaluatedKey.Count == 0)
                {
                    currentQueryResult = null;
                    return null;
                }

                await ReadRowsFromTable(0);
            }

            if (currentRowTransactions == null)
            {
                // TODO: assert currentRowTransactionsIndex = 0?
                currentRowTransactions = DeserializeCommitRecords(currentQueryResult[currentQueryResultIndex].Transactions);
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
            await storage.UpsertEntryAsync(this.options.TableName,
                new Dictionary<string, AttributeValue>
                {
                    { PartitionKey, new AttributeValue(StartRowPartitionKey) },
                    { RowKey, StartRowRowKey }
                },
                new Dictionary<string, AttributeValue>
                {
                    { AllocatedTransactionIdsKey, LongToAttribute(transactionId) }
                }).ConfigureAwait(false);

            startRecordValue = transactionId;
        }

        public async Task Append(IEnumerable<CommitRecord> commitRecords)
        {
            var batchOperation = new List<Dictionary<string, AttributeValue>>();

            // TODO modify this to be able to use IEnumerable, fixed size array for serialization in the size of CommitRecordsPerRow, list is temporary
            var transactionList = new List<CommitRecord>(commitRecords);

            for (int nextRecord = 0; nextRecord < transactionList.Count; nextRecord += CommitRecordsPerRow)
            {
                var recordCount = Math.Min(transactionList.Count - nextRecord, CommitRecordsPerRow);
                var transactionSegment = transactionList.GetRange(nextRecord, recordCount);
                var commitRow = new CommitRow(nextLogSequenceNumber);

                foreach (var transaction in transactionSegment)
                {
                    transaction.LSN = nextLogSequenceNumber++;
                }

                commitRow.Transactions = SerializeCommitRecords(transactionSegment);

                batchOperation.Add(
                    new Dictionary<string, AttributeValue>
                    {
                        { PartitionKey, new AttributeValue(CommitRecordPartitionKey) },
                        { RowKey, commitRow.FirstLSNAttribute },
                        { TransactionsKey, new AttributeValue { B = new MemoryStream(commitRow.Transactions.Value.Array) } }
                    });

                if (batchOperation.Count == BatchOperationLimit)
                {
                    await storage.PutEntriesAsync(this.options.TableName, batchOperation).ConfigureAwait(false);

                    batchOperation = new List<Dictionary<string, AttributeValue>>();
                }
            }

            if (batchOperation.Count > 0)
            {
                await storage.PutEntriesAsync(this.options.TableName, batchOperation).ConfigureAwait(false);
            }
        }

        public async Task TruncateLog(long lsn)
        {
            var keyValues = new Dictionary<string, AttributeValue>
            {
                { PartitionKeyAlias, new AttributeValue(CommitRecordPartitionKey) },
                { RowKeyAlias, LongToAttribute(lsn) }
            };
            string query = $"{PartitionKey} = {PartitionKeyAlias} AND {RowKey} <= {RowKeyAlias}";
            Dictionary<string, AttributeValue> lastEvaluatedKey = null;
            var batchOperation = new List<Dictionary<string, AttributeValue>>();

            do
            {
                var result = await storage.QueryAsync(this.options.TableName, keyValues, query,
                    CommitRowResolver, lastEvaluatedKey: lastEvaluatedKey).ConfigureAwait(false);
                lastEvaluatedKey = result.lastEvaluatedKey;

                foreach (var row in result.results)
                {
                    var transactions = DeserializeCommitRecords(row.Transactions);

                    if (transactions.Count > 0 && transactions[transactions.Count - 1].LSN <= lsn)
                    {
                        batchOperation.Add(
                            new Dictionary<string, AttributeValue>
                            {
                                    { PartitionKey, new AttributeValue(CommitRecordPartitionKey) },
                                    { RowKey, row.FirstLSNAttribute }
                            });

                        if (batchOperation.Count == BatchOperationLimit)
                        {
                            await storage.DeleteEntriesAsync(this.options.TableName, batchOperation).ConfigureAwait(false);

                            batchOperation = new List<Dictionary<string, AttributeValue>>();
                        }
                    }
                    else
                    {
                        break;
                    }
                }

            } while (lastEvaluatedKey.Count != 0);

            if (batchOperation.Count > 0)
            {
                await storage.DeleteEntriesAsync(this.options.TableName, batchOperation).ConfigureAwait(false);
            }
        }

        private async Task ReadRowsFromTable(long keyLowerBound)
        {
            var (results, lastEvaluatedKey) = await storage.QueryAsync(this.options.TableName,
                new Dictionary<string, AttributeValue>
                {
                    { PartitionKeyAlias, new AttributeValue(CommitRecordPartitionKey) },
                    { RowKeyAlias, LongToAttribute(keyLowerBound) }
                },
                $"{PartitionKey} = {PartitionKeyAlias} AND {RowKey} >= {RowKeyAlias}",
                CommitRowResolver,
                lastEvaluatedKey: currentLastEvaluatedKey).ConfigureAwait(false);
            currentQueryResult = results;

            // Reset the indexes
            currentQueryResultIndex = 0;
            currentRowTransactionsIndex = 0;
            currentLastEvaluatedKey = lastEvaluatedKey;
        }

        private ArraySegment<byte> SerializeCommitRecords(List<CommitRecord> commitRecords)
        {
            var serializableList = new List<Tuple<long, long, HashSet<ITransactionalResource>>>(commitRecords.Count);

            foreach (var commitRecord in commitRecords)
            {
                serializableList.Add(new Tuple<long, long, HashSet<ITransactionalResource>>(commitRecord.LSN, commitRecord.TransactionId, commitRecord.Resources));
            }

            var streamWriter = new BinaryTokenStreamWriter();

            serializationManager.Serialize(serializableList, streamWriter);

            return new ArraySegment<byte>(streamWriter.ToByteArray());
        }

        private List<CommitRecord> DeserializeCommitRecords(ArraySegment<byte>? serializerCommitRecords)
        {
            if (!serializerCommitRecords.HasValue)
            {
                return new List<CommitRecord>();
            }

            var streamReader = new BinaryTokenStreamReader(serializerCommitRecords.Value);

            var deserializedList = serializationManager.Deserialize<List<Tuple<long, long, HashSet<ITransactionalResource>>>>(streamReader);

            var commitRecords = new List<CommitRecord>(deserializedList.Count);

            foreach (var item in deserializedList)
            {
                commitRecords.Add(new CommitRecord { LSN = item.Item1, TransactionId = item.Item2, Resources = item.Item3 });
            }

            return commitRecords;
        }

        private static AttributeValue LongToAttribute(long value)
        {
            return new AttributeValue { N = value.ToString("d", CultureInfo.InvariantCulture) };
        }

        private static long AttributeToLong(AttributeValue value)
        {
            return long.Parse(value.N, CultureInfo.InvariantCulture);
        }

        private static Func<Dictionary<string, AttributeValue>, CommitRow> CommitRowResolver => (fields) =>
        {
            var commitRow = new CommitRow(AttributeToLong(fields[RowKey]));
            var stream = fields[TransactionsKey].B;
            if (stream.TryGetBuffer(out ArraySegment<byte> buffer))
            {
                commitRow.Transactions = buffer;
            }
            else
            {
                commitRow.Transactions = new ArraySegment<byte>(stream.ToArray());
            }
            return commitRow;
        };

        private class CommitRow
        {
            public CommitRow(long firstLSN)
            {
                FirstLSN = firstLSN;
            }

            public ArraySegment<byte>? Transactions { get; set; }

            public long FirstLSN { get; set; }

            public AttributeValue FirstLSNAttribute
            {
                get
                {
                    return LongToAttribute(FirstLSN);
                }
            }
        }

        private class StartRow
        {
            public StartRow(long transactionId)
            {
                AllocatedTransactionIds = transactionId;
            }

            public long AllocatedTransactionIds { get; set; }
        }
    }
}
