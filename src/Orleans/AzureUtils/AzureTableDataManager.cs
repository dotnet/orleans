/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Data.Services.Client;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using Microsoft.WindowsAzure.Storage.Shared.Protocol;
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
    public class AzureTableDataManager<T> where T: TableEntity, new()
    {
        /// <summary> Name of the table this instance is managing. </summary>
        public string TableName { get; private set; }

        /// <summary> TraceLogger for this table manager instance. </summary>
        protected internal TraceLogger Logger { get; private set; }

        /// <summary> Connection string for the Azure storage account used to host this table. </summary>
        protected string ConnectionString { get; set; }

        private readonly CloudTableClient tableOperationsClient;

        private const string ANY_ETAG = null; // Any Tag value is NULL and not "*" in WCF APIs (it is "*" in REST APIs);
        // See http://msdn.microsoft.com/en-us/library/windowsazure/dd894038.aspx

        private readonly CounterStatistic numServerBusy = CounterStatistic.FindOrCreate(StatisticNames.AZURE_SERVER_BUSY, true);

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="tableName">Name of the table to be connected to.</param>
        /// <param name="storageConnectionString">Connection string for the Azure storage account used to host this table.</param>
        public AzureTableDataManager(string tableName, string storageConnectionString, TraceLogger logger = null)
        {
            var loggerName = "AzureTableDataManager-" + typeof(T).Name;
            Logger = logger ?? TraceLogger.GetLogger(loggerName, TraceLogger.LoggerType.Runtime);
            TableName = tableName;
            ConnectionString = storageConnectionString;

            AzureStorageUtils.ValidateTableName(tableName);
            tableOperationsClient = GetCloudTableOperationsClient();
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
                CloudTable tableReference = tableCreationClient.GetTableReference(TableName);
                bool didCreate = await Task<bool>.Factory.FromAsync(
                     tableReference.BeginCreateIfNotExists,
                     tableReference.EndCreateIfNotExists,
                     null);

                Logger.Info(ErrorCode.AzureTable_01, "{0} Azure storage table {1}", (didCreate ? "Created" : "Attached to"), TableName);

                await InitializeTableSchemaFromEntity(tableCreationClient);

                Logger.Info(ErrorCode.AzureTable_36, "Initialized schema for Azure storage table {0}", TableName);
            }
            catch (Exception exc)
            {
                Logger.Error(ErrorCode.AzureTable_02, String.Format("Could not initialize connection to storage table {0}", TableName), exc);
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
                CloudTable tableReference = tableCreationClient.GetTableReference(TableName);
                bool didDelete = await Task<bool>.Factory.FromAsync(
                        tableReference.BeginDeleteIfExists,
                        tableReference.EndDeleteIfExists,
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

                CloudTable tableReference = tableOperationsClient.GetTableReference(TableName);
                try
                {
                    var opResult = await Task<TableResult>.Factory.FromAsync(
                        tableReference.BeginExecute,
                        tableReference.EndExecute,
                        TableOperation.Insert(data),
                        null);

                    return opResult.Etag;
                }
                catch (Exception exc)
                {
                    Logger.Warn(ErrorCode.AzureTable_05, String.Format("Intermediate error creating entry {0} in the table {1}",
                                (data == null ? "null" : data.ToString()), TableName), exc);
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
                CloudTable tableReference = tableOperationsClient.GetTableReference(TableName);
                try
                {
                    // WAS:
                    // svc.AttachTo(TableName, data, ANY_ETAG);
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
                    Logger.Warn(ErrorCode.AzureTable_06, String.Format("Intermediate error upserting entry {0} to the table {1}",
                        (data == null ? "null" : data.ToString()), TableName), exc);
                    throw;
                }
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }


        /// <summary>
        /// Merhes a data entry in the Azure table, without checking eTags.
        /// </summary>
        /// <param name="data">Data to be merged in the table.</param>
        /// <returns>Value promise with new Etag for this data entry after completing this storage operation.</returns>
        public async Task<string> MergeTableEntryAsync(T data)
        {
            const string operation = "MergeTableEntry";
            var startTime = DateTime.UtcNow;
            if (Logger.IsVerbose2) Logger.Verbose2("{0} entry {1} into table {2}", operation, data, TableName);

            try
            {
                CloudTable tableReference = tableOperationsClient.GetTableReference(TableName);
                try
                {
                    // WAS:
                    // svc.AttachTo(TableName, data, ANY_ETAG);
                    // svc.UpdateObject(data);
                    
                    data.ETag = "*";
                    //System.ArgumentException: Merge requires an ETag (which may be the '*' wildcard).
                    var opResult = await Task<TableResult>.Factory.FromAsync(
                          tableReference.BeginExecute,
                          tableReference.EndExecute,
                          TableOperation.Merge(data),
                          null);

                    return opResult.Etag;
                }
                catch (Exception exc)
                {
                    Logger.Warn(ErrorCode.AzureTable_07, String.Format("Intermediate error merging entry {0} to the table {1}",
                        (data == null ? "null" : data.ToString()), TableName), exc);
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
                    CloudTable tableReference = tableOperationsClient.GetTableReference(TableName);
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
        /// <returns>Completion promise for this storage operation.</returns>
        public async Task DeleteTableEntryAsync(T data, string eTag)
        {
            var list = new List<Tuple<T, string>> {new Tuple<T, string>(data, eTag)};
            await DeleteTableEntriesAsync(list);
        }

        /// <summary>
        /// Read a single table entry from the storage table.
        /// </summary>
        /// <param name="partitionKey">The partition key for the entry.</param>
        /// <param name="rowKey">The row key for the entry.</param>
        /// <returns>Value promise for tuple containing the data entry and its corresponding etag.</returns>
        public async Task<Tuple<T, string>> ReadSingleTableEntryAsync(string partitionKey, string rowKey)
        {
            Expression<Func<T, bool>> query = instance =>
                instance.PartitionKey == partitionKey
                && instance.RowKey == rowKey;

            var queryResults = await ReadTableEntriesAndEtagsAsync(query);
            var data = queryResults.ToList();
            if (data.Count >= 1) return data.First();

            if (Logger.IsVerbose) Logger.Verbose("Could not find table entry for PartitionKey={0} RowKey={1}", partitionKey, rowKey);
            return null;  // No data
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
            Expression<Func<T, bool>> query = _ => true;
            return ReadTableEntriesAndEtagsAsync(query);
        }

        #region Internal functions

        internal async Task<string> InsertTableEntryConditionallyAsync(T data, T tableVersion, string tableVersionEtag)
        {
            const string operation = "InsertTableEntryConditionally";
            string tableVersionData = (tableVersion == null ? "null" : tableVersion.ToString());
            var startTime = DateTime.UtcNow;
            
            if (Logger.IsVerbose2) Logger.Verbose2("{0} into table {1} version {2} entry {3}", operation, TableName, tableVersionData, data);

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

                    CloudTable tableReference = tableOperationsClient.GetTableReference(TableName);
                    var entityBatch = new TableBatchOperation();
                    entityBatch.Add(TableOperation.Insert(data));
                    tableVersion.ETag = tableVersionEtag;
                    entityBatch.Add(TableOperation.Replace(tableVersion));
                                                                               
                    var opResults = await Task<IList<TableResult>>.Factory.FromAsync(
                        tableReference.BeginExecuteBatch,
                        tableReference.EndExecuteBatch,
                        entityBatch,
                        null);

                    //The batch results are returned in order of execution,
                    //see reference at https://msdn.microsoft.com/en-us/library/microsoft.windowsazure.storage.table.cloudtable.executebatch.aspx.
                    //The ETag of data is needed in further operations.                    
                    return opResults[0].Etag;                                                                                                                                                              
                }
                catch (Exception exc)
                {
                    CheckAlertWriteError(operation, data, tableVersionData, exc);
                    throw;
                }                              
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }

        internal async Task<string> UpdateTableEntryConditionallyAsync(T data, string dataEtag, T tableVersion, string tableVersionEtag)
        {
            const string operation = "UpdateTableEntryConditionally";
            string tableVersionData = (tableVersion == null ? "null" : tableVersion.ToString());
            var startTime = DateTime.UtcNow;
            if (Logger.IsVerbose2) Logger.Verbose2("{0} table {1} version {2} entry {3}", operation, TableName, tableVersionData, data);

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

                    CloudTable tableReference = tableOperationsClient.GetTableReference(TableName);
                    var entityBatch = new TableBatchOperation();
                    data.ETag = dataEtag;
                    entityBatch.Add(TableOperation.Replace(data));
                    if (tableVersion != null && tableVersionEtag != null)
                    {
                        tableVersion.ETag = tableVersionEtag;
                        entityBatch.Add(TableOperation.Replace(tableVersion));
                    }
                                        
                    var opResults = await Task<IList<TableResult>>.Factory.FromAsync(
                        tableReference.BeginExecuteBatch,
                        tableReference.EndExecuteBatch,
                        entityBatch,
                        null);

                    //The batch results are returned in order of execution,
                    //see reference at https://msdn.microsoft.com/en-us/library/microsoft.windowsazure.storage.table.cloudtable.executebatch.aspx.
                    //The ETag of data is needed in further operations.                                        
                    return opResults[0].Etag;               
                }
                catch (Exception exc)
                {
                    CheckAlertWriteError(operation, data, tableVersionData, exc);
                    throw;                
                }
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }            
        }


        /// <summary>
        /// Deletes a set of already existing data entries in the table, by using eTag.
        /// Fails if the data does not already exist or if eTag does not match.
        /// </summary>
        /// <param name="list">List of data entries and their corresponding etags to be deleted from the table.</param>
        /// <returns>Completion promise for this storage operation.</returns>
        internal async Task DeleteTableEntriesAsync(IReadOnlyCollection<Tuple<T, string>> list)
        {
            const string operation = "DeleteTableEntries";
            var startTime = DateTime.UtcNow;
            if(Logger.IsVerbose2) Logger.Verbose2("Deleting {0} table entries: {1}", TableName, Utils.EnumerableToString(list));

            if(list == null || !list.Any())
            {
                return;
            }
            
            try
            {
                CloudTable tableReference = tableOperationsClient.GetTableReference(TableName);                
                var entityBatch = new TableBatchOperation();
                foreach(var tuple in list)
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
                        String.Format("Intermediate error deleting entries {0} from the table {1}.",
                            Utils.EnumerableToString(list), TableName), exc);
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
        internal async Task<IEnumerable<Tuple<T, string>>> ReadTableEntriesAndEtagsAsync(Expression<Func<T, bool>> predicate)
        {
            const string operation = "ReadTableEntriesAndEtags";
            var startTime = DateTime.UtcNow;

            try
            {
                CloudTable tableReference = tableOperationsClient.GetTableReference(TableName);                
                TableQuery<T> cloudTableQuery = tableReference.CreateQuery<T>().Where(predicate).AsTableQuery();
                try
                {
                    Func<Task<List<T>>> executeQueryHandleContinuations = async () =>
                    {
                        TableQuerySegment<T> querySegment = null;
                        var list = new List<T>();
                        while(querySegment == null || querySegment.ContinuationToken != null)
                        {
                            querySegment = await cloudTableQuery.ExecuteSegmentedAsync(querySegment != null ? querySegment.ContinuationToken : null);
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
                    return results.Select((T i) => Tuple.Create(i, i.ETag)).ToList();                    
                }
                catch (Exception exc)
                {
                    // Out of retries...
                    var errorMsg = string.Format("Failed to read Azure storage table {0}: {1}", TableName, exc.Message);
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

        internal async Task BulkInsertTableEntries(IReadOnlyCollection<T> data)
        {
            const string operation = "BulkInsertTableEntries";
            if (data == null) throw new ArgumentNullException("data");
            if (data.Count > AzureTableDefaultPolicies.MAX_BULK_UPDATE_ROWS)
            {
                throw new ArgumentOutOfRangeException("data", data.Count,
                        "Too many rows for bulk update - max " + AzureTableDefaultPolicies.MAX_BULK_UPDATE_ROWS);
            }

            var startTime = DateTime.UtcNow;
            if (Logger.IsVerbose2) Logger.Verbose2("Bulk inserting {0} entries to {1} table", data.Count, TableName);

            try
            {
                                
                // WAS:
                // svc.AttachTo(TableName, entry);
                // svc.UpdateObject(entry);
                // SaveChangesOptions.None | SaveChangesOptions.Batch,
                // SaveChangesOptions.None == Insert-or-merge operation, SaveChangesOptions.Batch == Batch transaction
                // http://msdn.microsoft.com/en-us/library/hh452241.aspx

                CloudTable tableReference = tableOperationsClient.GetTableReference(TableName);
                var entityBatch = new TableBatchOperation();
                foreach (T entry in data)
                {
                    entityBatch.Insert(entry);
                }

                bool fallbackToInsertOneByOne = false;
                try
                {
                    // http://msdn.microsoft.com/en-us/library/hh452241.aspx
                    await Task<IList<TableResult>>.Factory.FromAsync(
                        tableReference.BeginExecuteBatch,
                        tableReference.EndExecuteBatch,
                        entityBatch,
                        null);

                    return;
                }
                catch (Exception exc)
                {
                    Logger.Warn(ErrorCode.AzureTable_37, String.Format("Intermediate error bulk inserting {0} entries in the table {1}",
                        data.Count, TableName), exc);

                    var dsre = exc.GetBaseException() as DataServiceRequestException;
                    if (dsre != null)
                    {
                        var dsce = dsre.GetBaseException() as DataServiceClientException;
                        if (dsce != null)
                        {
                            // Fallback to insert rows one by one
                            fallbackToInsertOneByOne = true;
                        }
                    }

                    if(!fallbackToInsertOneByOne) throw;
                }

                // Bulk insert failed, so try to insert rows one by one instead
                var promises = new List<Task>();
                foreach (T entry in data)
                {
                    promises.Add(CreateTableEntryAsync(entry));
                }
                await Task.WhenAll(promises);
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
                return operationsClient;
            }
            catch (Exception exc)
            {
                Logger.Error(ErrorCode.AzureTable_17, String.Format("Error creating CloudTableOperationsClient."), exc);
                throw;
            }
        }

        private CloudTableClient GetCloudTableCreationClient()
        {
            try
            {
                CloudStorageAccount storageAccount = AzureStorageUtils.GetCloudStorageAccount(ConnectionString);
                CloudTableClient client = storageAccount.CreateCloudTableClient();
                client.DefaultRequestOptions.RetryPolicy = AzureTableDefaultPolicies.TableCreationRetryPolicy;
                client.DefaultRequestOptions.ServerTimeout = AzureTableDefaultPolicies.TableCreationTimeout;
                return client;
            }
            catch (Exception exc)
            {
                Logger.Error(ErrorCode.AzureTable_18, String.Format("Error creating CloudTableCreationClient."), exc);
                throw;
            }
        }

        // Based on: http://blogs.msdn.com/b/cesardelatorre/archive/2011/03/12/typical-issue-one-of-the-request-inputs-is-not-valid-when-working-with-the-wa-development-storage.aspx
        private async Task InitializeTableSchemaFromEntity(CloudTableClient tableClient)
        {
            const string operation = "InitializeTableSchemaFromEntity";
            var startTime = DateTime.UtcNow;

            TableEntity entity = new T();
            entity.PartitionKey = Guid.NewGuid().ToString();
            entity.RowKey = Guid.NewGuid().ToString();
            Array.ForEach(
                entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance),
                p =>
                {
                    if ((p.Name == "PartitionKey") || (p.Name == "RowKey") || (p.Name == "Timestamp")) return;

                    if (p.PropertyType == typeof(string))
                    {
                        p.SetValue(entity, Guid.NewGuid().ToString(),
                                   null);
                    }
                    else if (p.PropertyType == typeof(DateTime))
                    {
                        p.SetValue(entity, startTime, null);
                    }
                });

            try
            {
                // WAS:
                // svc.AddObject(TableName, entity);
                // SaveChangesOptions.None,

                CloudTable tableReference = tableClient.GetTableReference(TableName);
                try
                {
                    await Task<TableResult>.Factory.FromAsync(
                        tableReference.BeginExecute,
                        tableReference.EndExecute,
                        TableOperation.Insert(entity),
                        null);
                }
                catch (Exception exc)
                {
                    CheckAlertWriteError(operation + "-Create", entity, null, exc);
                    throw;
                }

                try
                {
                    // WAS:
                    // svc.DeleteObject(entity);
                    // SaveChangesOptions.None,

                    await Task<TableResult>.Factory.FromAsync(
                        tableReference.BeginExecute,
                        tableReference.EndExecute,
                        TableOperation.Delete(entity),
                        null);
                }
                catch (Exception exc)
                {
                    CheckAlertWriteError(operation + "-Delete", entity, null, exc);
                    throw;
                }
            }
            finally
            {
                CheckAlertSlowAccess(startTime, operation);
            }
        }

        private bool IsServerBusy(Exception exc)
        {
            string strCode = AzureStorageUtils.ExtractRestErrorCode(exc);
            bool serverBusy = StorageErrorCodeStrings.ServerBusy.Equals(strCode);
            if (serverBusy) numServerBusy.Increment();
            return serverBusy;
        }

        private void CheckAlertWriteError(string operation, object data, string tableVersionData, Exception exc)
        {
            HttpStatusCode httpStatusCode;
            string restStatus;
            if(AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus) && AzureStorageUtils.IsContentionError(httpStatusCode))
            {
                // log at Verbose, since failure on conditional is not not an error. Will analyze and warn later, if required.
                if(Logger.IsVerbose) Logger.Verbose(ErrorCode.AzureTable_13,
                   String.Format("Intermediate Azure table write error {0} to table {1} version {2} entry {3}",
                   operation, TableName, (tableVersionData ?? "null"), (data ?? "null")), exc);

            }
            else
            {
                Logger.Error(ErrorCode.AzureTable_14,
                    string.Format("Azure table access write error {0} to table {1} entry {2}", operation, TableName, data), exc);
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

        private static Type ResolveEntityType(string name)
        {
            return typeof(T);
        }

        #endregion
    }
}


