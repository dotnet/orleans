using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.WindowsAzure.Storage.Table.Queryable;
using Orleans.Runtime;

namespace Orleans.AzureUtils
{
    /// <summary>
    /// Utility class to encapsulate row-based access to Azure table storage.
    /// </summary>
    /// <remarks>
    /// These functions are mostly intended for internal usage by Orleans runtime, but due to certain assembly packaging constrants this class needs to have public visibility.
    /// </remarks>
    /// <typeparam name="T">Table data entry used by this table / manager.</typeparam>
    public class AzureTableDataManager<T> where T : class, ITableEntity, new()
    {
        /// <summary> Name of the table this instance is managing. </summary>
        public string TableName { get; private set; }

        /// <summary> Logger for this table manager instance. </summary>
        protected internal Logger Logger { get; private set; }

        /// <summary> Connection string for the Azure storage account used to host this table. </summary>
        protected string ConnectionString { get; set; }

        private CloudTable tableReference;

        private readonly CounterStatistic numServerBusy = CounterStatistic.FindOrCreate(StatisticNames.AZURE_SERVER_BUSY, true);

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="tableName">Name of the table to be connected to.</param>
        /// <param name="storageConnectionString">Connection string for the Azure storage account used to host this table.</param>
        /// <param name="logger">Logger to use.</param>
        public AzureTableDataManager(string tableName, string storageConnectionString, Logger logger = null)
        {
            var loggerName = "AzureTableDataManager-" + typeof(T).Name;
            Logger = logger ?? LogManager.GetLogger(loggerName, LoggerType.Runtime);
            TableName = tableName;
            ConnectionString = storageConnectionString;

            AzureStorageUtils.ValidateTableName(tableName);
        }

        /// <summary>
        /// Connects to, or creates and initializes a new Azure table if it does not already exist.
        /// </summary>
        /// <returns>Completion promise for this operation.</returns>
        public async Task InitTableAsync()
        {
            const string operation = "InitTable";
            var startTime = DateTime.UtcNow;

            try
            {
                CloudTableClient tableCreationClient = GetCloudTableCreationClient();
                CloudTable tableRef = tableCreationClient.GetTableReference(TableName);
                bool didCreate = await Task<bool>.Factory.FromAsync(
                     tableRef.BeginCreateIfNotExists,
                     tableRef.EndCreateIfNotExists,
                     null);

                Logger.Info(ErrorCode.AzureTable_01, "{0} Azure storage table {1}", (didCreate ? "Created" : "Attached to"), TableName);

                CloudTableClient tableOperationsClient = GetCloudTableOperationsClient();
                tableReference = tableOperationsClient.GetTableReference(TableName);
            }
            catch (Exception exc)
            {
                Logger.Error(ErrorCode.AzureTable_02, $"Could not initialize connection to storage table {TableName}", exc);
                throw;
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }

        /// <summary>
        /// Deletes the Azure table.
        /// </summary>
        /// <returns>Completion promise for this operation.</returns>
        public async Task DeleteTableAsync()
        {
            const string operation = "DeleteTable";
            var startTime = DateTime.UtcNow;

            try
            {
                CloudTableClient tableCreationClient = GetCloudTableCreationClient();
                CloudTable tableRef = tableCreationClient.GetTableReference(TableName);
                bool didDelete = await Task<bool>.Factory.FromAsync(
                        tableRef.BeginDeleteIfExists,
                        tableRef.EndDeleteIfExists,
                        null);

                if (didDelete)
                {
                    Logger.Info(ErrorCode.AzureTable_03, "Deleted Azure storage table {0}", TableName);
                }
            }
            catch (Exception exc)
            {
                Logger.Error(ErrorCode.AzureTable_04, "Could not delete storage table {0}", exc);
                throw;
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }

        /// <summary>
        /// Deletes all entities the Azure table.
        /// </summary>
        /// <returns>Completion promise for this operation.</returns>
        public async Task ClearTableAsync()
        {
            IEnumerable<Tuple<T,string>> items = await ReadAllTableEntriesAsync();
            IEnumerable<Task> work = items.GroupBy(item => item.Item1.PartitionKey)
                                          .SelectMany(partition => partition.ToBatch(AzureTableDefaultPolicies.MAX_BULK_UPDATE_ROWS))
                                          .Select(batch => DeleteTableEntriesAsync(batch.ToList()));
            await Task.WhenAll(work);
        }

        /// <summary>
        /// Create a new data entry in the Azure table (insert new, not update existing).
        /// Fails if the data already exists.
        /// </summary>
        /// <param name="data">Data to be inserted into the table.</param>
        /// <returns>Value promise with new Etag for this data entry after completing this storage operation.</returns>
        public async Task<string> CreateTableEntryAsync(T data)
        {
            const string operation = "CreateTableEntry";
            var startTime = DateTime.UtcNow;

            if (Logger.IsVerbose2) Logger.Verbose2("Creating {0} table entry: {1}", TableName, data);

            try
            {
                // WAS:
                // svc.AddObject(TableName, data);
                // SaveChangesOptions.None

                try
                {
                    // Presumably FromAsync(BeginExecute, EndExecute) has a slightly better performance then CreateIfNotExistsAsync.
                    var opResult = await Task<TableResult>.Factory.FromAsync(
                        tableReference.BeginExecute,
                        tableReference.EndExecute,
                        TableOperation.Insert(data),
                        null);

                    return opResult.Etag;
                }
                catch (Exception exc)
                {
                    CheckAlertWriteError(operation, data, null, exc);
                    throw;
                }
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }

        /// <summary>
        /// Inserts a data entry in the Azure table: creates a new one if does not exists or overwrites (without eTag) an already existing version (the "update in place" semantincs).
        /// </summary>
        /// <param name="data">Data to be inserted or replaced in the table.</param>
        /// <returns>Value promise with new Etag for this data entry after completing this storage operation.</returns>
        public async Task<string> UpsertTableEntryAsync(T data)
        {
            const string operation = "UpsertTableEntry";
            var startTime = DateTime.UtcNow;
            if (Logger.IsVerbose2) Logger.Verbose2("{0} entry {1} into table {2}", operation, data, TableName);

            try
            {
                try
                {
                    // WAS:
                    // svc.AttachTo(TableName, data, null);
                    // svc.UpdateObject(data);
                    // SaveChangesOptions.ReplaceOnUpdate,

                    var opResult = await Task<TableResult>.Factory.FromAsync(
                       tableReference.BeginExecute,
                       tableReference.EndExecute,
                       TableOperation.InsertOrReplace(data),
                       null);
                    
                    return opResult.Etag;                                                           
                }
                catch (Exception exc)
                {
                    Logger.Warn(ErrorCode.AzureTable_06,
                        $"Intermediate error upserting entry {(data == null ? "null" : data.ToString())} to the table {TableName}", exc);
                    throw;
                }
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }


        /// <summary>
        /// Merges a data entry in the Azure table.
        /// </summary>
        /// <param name="data">Data to be merged in the table.</param>
        /// <param name="eTag">ETag to apply.</param>
        /// <returns>Value promise with new Etag for this data entry after completing this storage operation.</returns>
        internal async Task<string> MergeTableEntryAsync(T data, string eTag)
        {
            const string operation = "MergeTableEntry";
            var startTime = DateTime.UtcNow;
            if (Logger.IsVerbose2) Logger.Verbose2("{0} entry {1} into table {2}", operation, data, TableName);

            try
            {
               
                try
                {
                    // WAS:
                    // svc.AttachTo(TableName, data, ANY_ETAG);
                    // svc.UpdateObject(data);

                    data.ETag = eTag;
                    // Merge requires an ETag (which may be the '*' wildcard).
                    var opResult = await Task<TableResult>.Factory.FromAsync(
                          tableReference.BeginExecute,
                          tableReference.EndExecute,
                          TableOperation.Merge(data),
                          null);

                    return opResult.Etag;
                }
                catch (Exception exc)
                {
                    Logger.Warn(ErrorCode.AzureTable_07,
                        $"Intermediate error merging entry {(data == null ? "null" : data.ToString())} to the table {TableName}", exc);
                    throw;
                }
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }

        /// <summary>
        /// Updates a data entry in the Azure table: updates an already existing data in the table, by using eTag.
        /// Fails if the data does not already exist or of eTag does not match.
        /// </summary>
        /// <param name="data">Data to be updated into the table.</param>
        /// /// <param name="dataEtag">ETag to use.</param>
        /// <returns>Value promise with new Etag for this data entry after completing this storage operation.</returns>
        public async Task<string> UpdateTableEntryAsync(T data, string dataEtag)
        {
            const string operation = "UpdateTableEntryAsync";
            var startTime = DateTime.UtcNow;
            if (Logger.IsVerbose2) Logger.Verbose2("{0} table {1}  entry {2}", operation, TableName, data);

            try
            {
                try
                {
                    data.ETag = dataEtag;

                    var opResult = await Task<TableResult>.Factory.FromAsync(
                        tableReference.BeginExecute,
                        tableReference.EndExecute,
                        TableOperation.Replace(data),
                        null);

                    //The ETag of data is needed in further operations.                                        
                    return opResult.Etag;
                }
                catch (Exception exc)
                {
                    CheckAlertWriteError(operation, data, null, exc);
                    throw;
                }
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }

        /// <summary>
        /// Deletes an already existing data in the table, by using eTag.
        /// Fails if the data does not already exist or if eTag does not match.
        /// </summary>
        /// <param name="data">Data entry to be deleted from the table.</param>
        /// <param name="eTag">ETag to use.</param>
        /// <returns>Completion promise for this storage operation.</returns>
        public async Task DeleteTableEntryAsync(T data, string eTag)
        {            
            const string operation = "DeleteTableEntryAsync";
            var startTime = DateTime.UtcNow;
            if (Logger.IsVerbose2) Logger.Verbose2("{0} table {1}  entry {2}", operation, TableName, data);

            try
            {   
                data.ETag = eTag;
                
                try
                {
                    // Presumably FromAsync(BeginExecute, EndExecute) has a slightly better performance then DeleteIfExistsAsync.
                    await Task<TableResult>.Factory.FromAsync(
                        tableReference.BeginExecute,
                        tableReference.EndExecute,
                        TableOperation.Delete(data),
                        null);
                }
                catch (Exception exc)
                {
                    Logger.Warn(ErrorCode.AzureTable_08,
                        $"Intermediate error deleting entry {data} from the table {TableName}.", exc);
                    throw;
                }
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }

        /// <summary>
        /// Read a single table entry from the storage table.
        /// </summary>
        /// <param name="partitionKey">The partition key for the entry.</param>
        /// <param name="rowKey">The row key for the entry.</param>
        /// <returns>Value promise for tuple containing the data entry and its corresponding etag.</returns>
        public async Task<Tuple<T, string>> ReadSingleTableEntryAsync(string partitionKey, string rowKey)
        {
            const string operation = "ReadSingleTableEntryAsync";
            var startTime = DateTime.UtcNow;
            if (Logger.IsVerbose2) Logger.Verbose2("{0} table {1} partitionKey {2} rowKey = {3}", operation, TableName, partitionKey, rowKey);
            T retrievedResult = default(T);

            try
            {
                try
                {
                    string queryString = TableQueryFilterBuilder.MatchPartitionKeyAndRowKeyFilter(partitionKey, rowKey);
                    var query = new TableQuery<T>().Where(queryString);
                    TableQuerySegment<T> segment = await Task.Factory
                        .FromAsync<TableQuery<T>, TableContinuationToken, TableQuerySegment<T>>(
                            tableReference.BeginExecuteQuerySegmented,
                            tableReference.EndExecuteQuerySegmented<T>, query, null, null);
                    retrievedResult = segment.Results.SingleOrDefault();
                }
                catch (StorageException exception)
                {
                    if (!AzureStorageUtils.TableStorageDataNotFound(exception))
                        throw;
                }
                //The ETag of data is needed in further operations.                                        
                if (retrievedResult != null) return new Tuple<T, string>(retrievedResult, retrievedResult.ETag);
                if (Logger.IsVerbose) Logger.Verbose("Could not find table entry for PartitionKey={0} RowKey={1}", partitionKey, rowKey);
                return null;  // No data
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }

        /// <summary>
        /// Read all entries in one partition of the storage table.
        /// NOTE: This could be an expensive and slow operation for large table partitions!
        /// </summary>
        /// <param name="partitionKey">The key for the partition to be searched.</param>
        /// <returns>Enumeration of all entries in the specified table partition.</returns>
        public Task<IEnumerable<Tuple<T, string>>> ReadAllTableEntriesForPartitionAsync(string partitionKey)
        {
            Expression<Func<T, bool>> query = instance =>
                instance.PartitionKey == partitionKey;

            return ReadTableEntriesAndEtagsAsync(query);
        }

        /// <summary>
        /// Read all entries in the table.
        /// NOTE: This could be a very expensive and slow operation for large tables!
        /// </summary>
        /// <returns>Enumeration of all entries in the table.</returns>
        public Task<IEnumerable<Tuple<T, string>>> ReadAllTableEntriesAsync()
        {
            return ReadTableEntriesAndEtagsAsync(null);
        }

        /// <summary>
        /// Deletes a set of already existing data entries in the table, by using eTag.
        /// Fails if the data does not already exist or if eTag does not match.
        /// </summary>
        /// <param name="collection">Data entries and their corresponding etags to be deleted from the table.</param>
        /// <returns>Completion promise for this storage operation.</returns>
        public async Task DeleteTableEntriesAsync(IReadOnlyCollection<Tuple<T, string>> collection)
        {
            const string operation = "DeleteTableEntries";
            var startTime = DateTime.UtcNow;
            if (Logger.IsVerbose2) Logger.Verbose2("Deleting {0} table entries: {1}", TableName, Utils.EnumerableToString(collection));

            if (collection == null) throw new ArgumentNullException("collection");

            if (collection.Count > AzureTableDefaultPolicies.MAX_BULK_UPDATE_ROWS)
            {
                throw new ArgumentOutOfRangeException("collection", collection.Count,
                        "Too many rows for bulk delete - max " + AzureTableDefaultPolicies.MAX_BULK_UPDATE_ROWS);
            }

            if (collection.Count == 0)
            {
                return;
            }

            try
            {
                var entityBatch = new TableBatchOperation();
                foreach (var tuple in collection)
                {
                    // WAS:
                    // svc.AttachTo(TableName, tuple.Item1, tuple.Item2);
                    // svc.DeleteObject(tuple.Item1);
                    // SaveChangesOptions.ReplaceOnUpdate | SaveChangesOptions.Batch,
                    T item = tuple.Item1;
                    item.ETag = tuple.Item2;
                    entityBatch.Delete(item);
                }

                try
                {
                    await Task<IList<TableResult>>.Factory.FromAsync(
                        tableReference.BeginExecuteBatch,
                        tableReference.EndExecuteBatch,
                        entityBatch,
                        null);
                }
                catch (Exception exc)
                {
                    Logger.Warn(ErrorCode.AzureTable_08,
                        $"Intermediate error deleting entries {Utils.EnumerableToString(collection)} from the table {TableName}.", exc);
                    throw;
                }
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }

        /// <summary>
        /// Read data entries and their corresponding eTags from the Azure table.
        /// </summary>
        /// <param name="predicate">Predicate function to use for querying the table and filtering the results.</param>
        /// <returns>Enumeration of entries in the table which match the query condition.</returns>
        public async Task<IEnumerable<Tuple<T, string>>> ReadTableEntriesAndEtagsAsync(Expression<Func<T, bool>> predicate)
        {
            const string operation = "ReadTableEntriesAndEtags";
            var startTime = DateTime.UtcNow;

            try
            {
                TableQuery<T> cloudTableQuery = predicate == null 
                    ? tableReference.CreateQuery<T>()
                    : tableReference.CreateQuery<T>().Where(predicate).AsTableQuery();
                try
                {
                    Func<Task<List<T>>> executeQueryHandleContinuations = async () =>
                    {
                        TableQuerySegment<T> querySegment = null;
                        var list = new List<T>();
                        while (querySegment == null || querySegment.ContinuationToken != null)
                        {
                            querySegment = await cloudTableQuery.ExecuteSegmentedAsync(querySegment?.ContinuationToken);
                            list.AddRange(querySegment);
                        }

                        return list;
                    };

                    IBackoffProvider backoff = new FixedBackoff(AzureTableDefaultPolicies.PauseBetweenTableOperationRetries);

                    List<T> results = await AsyncExecutorWithRetries.ExecuteWithRetries(
                        counter => executeQueryHandleContinuations(),
                        AzureTableDefaultPolicies.MaxTableOperationRetries,
                        (exc, counter) => AzureStorageUtils.AnalyzeReadException(exc.GetBaseException(), counter, TableName, Logger),
                        AzureTableDefaultPolicies.TableOperationTimeout,
                        backoff);

                    // Data was read successfully if we got to here                    
                    return results.Select(i => Tuple.Create(i, i.ETag)).ToList();
                }
                catch (Exception exc)
                {
                    // Out of retries...
                    var errorMsg = $"Failed to read Azure storage table {TableName}: {exc.Message}";
                    if (!AzureStorageUtils.TableStorageDataNotFound(exc))
                    {
                        Logger.Warn(ErrorCode.AzureTable_09, errorMsg, exc);
                    }
                    throw new OrleansException(errorMsg, exc);
                }
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }

        /// <summary>
        /// Inserts a set of new data entries into the table.
        /// Fails if the data does already exists.
        /// </summary>
        /// <param name="collection">Data entries to be inserted into the table.</param>
        /// <returns>Completion promise for this storage operation.</returns>
        public async Task BulkInsertTableEntries(IReadOnlyCollection<T> collection)
        {
            const string operation = "BulkInsertTableEntries";
            if (collection == null) throw new ArgumentNullException("collection");
            if (collection.Count > AzureTableDefaultPolicies.MAX_BULK_UPDATE_ROWS)
            {
                throw new ArgumentOutOfRangeException("collection", collection.Count,
                        "Too many rows for bulk update - max " + AzureTableDefaultPolicies.MAX_BULK_UPDATE_ROWS);
            }

            if (collection.Count == 0)
            {
                return;
            }

            var startTime = DateTime.UtcNow;
            if (Logger.IsVerbose2) Logger.Verbose2("Bulk inserting {0} entries to {1} table", collection.Count, TableName);

            try
            {

                // WAS:
                // svc.AttachTo(TableName, entry);
                // svc.UpdateObject(entry);
                // SaveChangesOptions.None | SaveChangesOptions.Batch,
                // SaveChangesOptions.None == Insert-or-merge operation, SaveChangesOptions.Batch == Batch transaction
                // http://msdn.microsoft.com/en-us/library/hh452241.aspx

                var entityBatch = new TableBatchOperation();
                foreach (T entry in collection)
                {
                    entityBatch.Insert(entry);
                }

                try
                {
                    // http://msdn.microsoft.com/en-us/library/hh452241.aspx
                    await Task<IList<TableResult>>.Factory.FromAsync(
                        tableReference.BeginExecuteBatch,
                        tableReference.EndExecuteBatch,
                        entityBatch,
                        null);
                }
                catch (Exception exc)
                {
                    Logger.Warn(ErrorCode.AzureTable_37,
                        $"Intermediate error bulk inserting {collection.Count} entries in the table {TableName}", exc);
                }
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }

        #region Internal functions

        internal async Task<Tuple<string, string>> InsertTwoTableEntriesConditionallyAsync(T data1, T data2, string data2Etag)
        {
            const string operation = "InsertTableEntryConditionally";
            string data2Str = (data2 == null ? "null" : data2.ToString());
            var startTime = DateTime.UtcNow;

            if (Logger.IsVerbose2) Logger.Verbose2("{0} into table {1} data1 {2} data2 {3}", operation, TableName, data1, data2Str);

            try
            {                                
                try
                {
                    // WAS:
                    // Only AddObject, do NOT AttachTo. If we did both UpdateObject and AttachTo, it would have been equivalent to InsertOrReplace.
                    // svc.AddObject(TableName, data);
                    // --- 
                    // svc.AttachTo(TableName, tableVersion, tableVersionEtag);
                    // svc.UpdateObject(tableVersion);
                    // SaveChangesOptions.ReplaceOnUpdate | SaveChangesOptions.Batch, 
                    // EntityDescriptor dataResult = svc.GetEntityDescriptor(data);
                    // return dataResult.ETag;

                    var entityBatch = new TableBatchOperation();
                    entityBatch.Add(TableOperation.Insert(data1));
                    data2.ETag = data2Etag;
                    entityBatch.Add(TableOperation.Replace(data2));
                                                                               
                    var opResults = await Task<IList<TableResult>>.Factory.FromAsync(
                        tableReference.BeginExecuteBatch,
                        tableReference.EndExecuteBatch,
                        entityBatch,
                        null);

                    //The batch results are returned in order of execution,
                    //see reference at https://msdn.microsoft.com/en-us/library/microsoft.windowsazure.storage.table.cloudtable.executebatch.aspx.
                    //The ETag of data is needed in further operations.                    
                    return new Tuple<string, string>(opResults[0].Etag, opResults[1].Etag);                                                                                                                                                              
                }
                catch (Exception exc)
                {
                    CheckAlertWriteError(operation, data1, data2Str, exc);
                    throw;
                }                              
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }

        internal async Task<Tuple<string, string>> UpdateTwoTableEntriesConditionallyAsync(T data1, string data1Etag, T data2, string data2Etag)
        {
            const string operation = "UpdateTableEntryConditionally";
            string data2Str = (data2 == null ? "null" : data2.ToString());
            var startTime = DateTime.UtcNow;
            if (Logger.IsVerbose2) Logger.Verbose2("{0} table {1} data1 {2} data2 {3}", operation, TableName, data1, data2Str);

            try
            {
                try
                {
                    // WAS:
                    // Only AddObject, do NOT AttachTo. If we did both UpdateObject and AttachTo, it would have been equivalent to InsertOrReplace.
                    // svc.AttachTo(TableName, data, dataEtag);
                    // svc.UpdateObject(data);
                    // ----
                    // svc.AttachTo(TableName, tableVersion, tableVersionEtag);
                    // svc.UpdateObject(tableVersion);
                    // SaveChangesOptions.ReplaceOnUpdate | SaveChangesOptions.Batch, 
                    // EntityDescriptor dataResult = svc.GetEntityDescriptor(data);
                    // return dataResult.ETag;

                    var entityBatch = new TableBatchOperation();
                    data1.ETag = data1Etag;
                    entityBatch.Add(TableOperation.Replace(data1));
                    if (data2 != null && data2Etag != null)
                    {
                        data2.ETag = data2Etag;
                        entityBatch.Add(TableOperation.Replace(data2));
                    }
                                        
                    var opResults = await Task<IList<TableResult>>.Factory.FromAsync(
                        tableReference.BeginExecuteBatch,
                        tableReference.EndExecuteBatch,
                        entityBatch,
                        null);

                    //The batch results are returned in order of execution,
                    //see reference at https://msdn.microsoft.com/en-us/library/microsoft.windowsazure.storage.table.cloudtable.executebatch.aspx.
                    //The ETag of data is needed in further operations.                                        
                    return new Tuple<string, string>(opResults[0].Etag, opResults[1].Etag);                   
                }
                catch (Exception exc)
                {
                    CheckAlertWriteError(operation, data1, data2Str, exc);
                    throw;                
                }
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }            
        }

        // Utility methods

        private CloudTableClient GetCloudTableOperationsClient()
        {
            try
            {
                CloudStorageAccount storageAccount = AzureStorageUtils.GetCloudStorageAccount(ConnectionString);
                CloudTableClient operationsClient = storageAccount.CreateCloudTableClient();
                operationsClient.DefaultRequestOptions.RetryPolicy = AzureTableDefaultPolicies.TableOperationRetryPolicy;
                operationsClient.DefaultRequestOptions.ServerTimeout = AzureTableDefaultPolicies.TableOperationTimeout;
                // Values supported can be AtomPub, Json, JsonFullMetadata or JsonNoMetadata with Json being the default value
                operationsClient.DefaultRequestOptions.PayloadFormat = TablePayloadFormat.JsonNoMetadata;
                return operationsClient;
            }
            catch (Exception exc)
            {
                Logger.Error(ErrorCode.AzureTable_17, "Error creating CloudTableOperationsClient.", exc);
                throw;
            }
        }

        private CloudTableClient GetCloudTableCreationClient()
        {
            try
            {
                CloudStorageAccount storageAccount = AzureStorageUtils.GetCloudStorageAccount(ConnectionString);
                CloudTableClient creationClient = storageAccount.CreateCloudTableClient();
                creationClient.DefaultRequestOptions.RetryPolicy = AzureTableDefaultPolicies.TableCreationRetryPolicy;
                creationClient.DefaultRequestOptions.ServerTimeout = AzureTableDefaultPolicies.TableCreationTimeout;
                // Values supported can be AtomPub, Json, JsonFullMetadata or JsonNoMetadata with Json being the default value
                creationClient.DefaultRequestOptions.PayloadFormat = TablePayloadFormat.JsonNoMetadata;
                return creationClient;
            }
            catch (Exception exc)
            {
                Logger.Error(ErrorCode.AzureTable_18, "Error creating CloudTableCreationClient.", exc);
                throw;
            }
        }

        private void CheckAlertWriteError(string operation, object data1, string data2, Exception exc)
        {
            HttpStatusCode httpStatusCode;
            string restStatus;
            if(AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus) && AzureStorageUtils.IsContentionError(httpStatusCode))
            {
                // log at Verbose, since failure on conditional is not not an error. Will analyze and warn later, if required.
                if(Logger.IsVerbose) Logger.Verbose(ErrorCode.AzureTable_13,
                    $"Intermediate Azure table write error {operation} to table {TableName} data1 {(data1 ?? "null")} data2 {(data2 ?? "null")}", exc);

            }
            else
            {
                Logger.Error(ErrorCode.AzureTable_14,
                    $"Azure table access write error {operation} to table {TableName} entry {data1}", exc);
            }
        }

        private void CheckAlertSlowAccess(DateTime startOperation, string operation)
        {
            var timeSpan = DateTime.UtcNow - startOperation;
            if (timeSpan > AzureTableDefaultPolicies.TableOperationTimeout)
            {
                Logger.Warn(ErrorCode.AzureTable_15, "Slow access to Azure Table {0} for {1}, which took {2}.", TableName, operation, timeSpan);
            }
        }

        #endregion
    }

    internal static class TableDataManagerInternalExtensions
    {
        internal static IEnumerable<IEnumerable<TItem>> ToBatch<TItem>(this IEnumerable<TItem> source, int size)
        {
            using (IEnumerator<TItem> enumerator = source.GetEnumerator())
                while (enumerator.MoveNext())
                    yield return Take(enumerator, size);
        }

        private static IEnumerable<TItem> Take<TItem>(IEnumerator<TItem> source, int size)
        {
            int i = 0;
            do
                yield return source.Current;
            while (++i < size && source.MoveNext());
        }
    }
}