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
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.StorageClient;
using Orleans.Runtime;

namespace Orleans.AzureUtils
{
    /// <summary>
    /// Utility class to encapsulate row-based access to Azure table storage .
    /// </summary>
    /// <remarks>
    /// These functions are mostly intended for internal usage by Orleans runtime, but due to certain assembly packaging constrants this class needs to have public visibility.
    /// </remarks>
    /// <typeparam name="T">Table data entry used by this table / manager.</typeparam>
    public class AzureTableDataManager<T> where T : TableServiceEntity, new()
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

                bool didCreate = await Task<bool>.Factory.FromAsync(
                     tableCreationClient.BeginCreateTableIfNotExist,
                     tableCreationClient.EndCreateTableIfNotExist,
                     TableName,
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

                bool didDelete = await Task<bool>.Factory.FromAsync(
                        tableCreationClient.BeginDeleteTableIfExist,
                        tableCreationClient.EndDeleteTableIfExist,
                        TableName,
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
                TableServiceContext svc = tableOperationsClient.GetDataServiceContext();
                svc.AddObject(TableName, data);

                try
                {
                    await Task<DataServiceResponse>.Factory.FromAsync(
                        svc.BeginSaveChangesWithRetries,
                        svc.EndSaveChangesWithRetries,
                        SaveChangesOptions.None,
                        null);

                    return svc.GetEntityDescriptor(data).ETag;
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
                TableServiceContext svc = tableOperationsClient.GetDataServiceContext();
                try
                {
                    Task<DataServiceResponse> savePromise;

                    Func<int, Task<DataServiceResponse>> doSaveChanges = retryNum =>
                    {
                        if (retryNum > 0) svc.Detach(data);

                        // Try to do update first
                        svc.AttachTo(TableName, data, ANY_ETAG);
                        svc.UpdateObject(data);

                        return Task<DataServiceResponse>.Factory.FromAsync(
                                svc.BeginSaveChangesWithRetries,
                                svc.EndSaveChangesWithRetries,
                                SaveChangesOptions.ReplaceOnUpdate,
                                null);
                    };


                    if (AzureTableDefaultPolicies.MaxBusyRetries > 0)
                    {
                        IBackoffProvider backoff = new FixedBackoff(AzureTableDefaultPolicies.PauseBetweenBusyRetries);
                        
                        savePromise = AsyncExecutorWithRetries.ExecuteWithRetries(
                            doSaveChanges,
                            AzureTableDefaultPolicies.MaxBusyRetries,
                            // Retry automatically iff we get ServerBusy reply from Azure storage
                            (exc, retryNum) => IsServerBusy(exc),
                            AzureTableDefaultPolicies.BusyRetriesTimeout,
                            backoff);
                    }
                    else
                    {
                        // Try single Write only once
                        savePromise = doSaveChanges(0);
                    }
                    await savePromise;
                    EntityDescriptor result = svc.GetEntityDescriptor(data);
                    return result.ETag;
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
        /// Updates a data entry in the Azure table: updates an already existing data in the table, by using eTag.
        /// Fails if the data does not already exist or of eTag does not match.
        /// </summary>
        /// <param name="data">Data to be updated into the table.</param>
        /// <returns>Value promise with new Etag for this data entry after completing this storage operation.</returns>
        public Task<string> UpdateTableEntryAsync(T data, string dataEtag)
        {
            return UpdateTableEntryConditionallyAsync(data, dataEtag, null, null);
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
        /// <returns>Enumeration of all entries in the table</returns>
        public Task<IEnumerable<Tuple<T, string>>> ReadAllTableEntriesAsync()
        {
            Expression<Func<T, bool>> query = _ => true;
            return ReadTableEntriesAndEtagsAsync(query);
        }

        #region Internal functions

        internal async Task<string> InsertTableEntryConditionallyAsync(T data, T tableVersion, string tableVersionEtag, bool updateTableVersion = true)
        {
            const string operation = "InsertTableEntryConditionally";
            string tableVersionData = (tableVersion == null ? "null" : tableVersion.ToString());
            var startTime = DateTime.UtcNow;
            
            if (Logger.IsVerbose2) Logger.Verbose2("{0} into table {1} version {2} entry {3}", operation, TableName, tableVersionData, data);

            try
            {
                TableServiceContext svc = tableOperationsClient.GetDataServiceContext();
                // Only AddObject, do NOT AttachTo. If we did both UpdateObject and AttachTo, it would have been equivalent to InsertOrReplace.
                svc.AddObject(TableName, data);
                if (updateTableVersion)
                {
                    svc.AttachTo(TableName, tableVersion, tableVersionEtag);
                    svc.UpdateObject(tableVersion);
                }
                try
                {
                    await Task<DataServiceResponse>.Factory.FromAsync(
                        svc.BeginSaveChangesWithRetries, 
                        svc.EndSaveChangesWithRetries, 
                        SaveChangesOptions.ReplaceOnUpdate | SaveChangesOptions.Batch, 
                        null);

                    EntityDescriptor dataResult = svc.GetEntityDescriptor(data);
                    return dataResult.ETag;
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
                TableServiceContext svc = tableOperationsClient.GetDataServiceContext();
                svc.AttachTo(TableName, data, dataEtag);
                svc.UpdateObject(data);
                if (tableVersion != null && tableVersionEtag != null)
                {
                    svc.AttachTo(TableName, tableVersion, tableVersionEtag);
                    svc.UpdateObject(tableVersion);
                }

                try
                {
                    await Task<DataServiceResponse>.Factory.FromAsync(
                        svc.BeginSaveChangesWithRetries,
                        svc.EndSaveChangesWithRetries,
                        SaveChangesOptions.ReplaceOnUpdate | SaveChangesOptions.Batch,
                        null);

                    EntityDescriptor dataResult = svc.GetEntityDescriptor(data);
                    return dataResult.ETag;
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

        internal async Task<string> MergeTableEntryAsync(T data)
        {
            const string operation = "MergeTableEntry";
            var startTime = DateTime.UtcNow;
            if (Logger.IsVerbose2) Logger.Verbose2("{0} entry {1} into table {2}", operation, data, TableName);

            try
            {
                TableServiceContext svc = tableOperationsClient.GetDataServiceContext();
                svc.AttachTo(TableName, data, ANY_ETAG);
                svc.UpdateObject(data);

                try
                {
                    await Task<DataServiceResponse>.Factory.FromAsync(
                        svc.BeginSaveChangesWithRetries,
                        svc.EndSaveChangesWithRetries,
                        SaveChangesOptions.None,
                        null);

                    return svc.GetEntityDescriptor(data).ETag;
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
        /// Deletes a set of already existing data entries in the table, by using eTag.
        /// Fails if the data does not already exist or if eTag does not match.
        /// </summary>
        /// <param name="list">List of data entries and their corresponding etags to be deleted from the table.</param>
        /// <returns>Completion promise for this storage operation.</returns>
        internal async Task DeleteTableEntriesAsync(IReadOnlyCollection<Tuple<T, string>> list)
        {
            const string operation = "DeleteTableEntries";
            var startTime = DateTime.UtcNow;
            if (Logger.IsVerbose2) Logger.Verbose2("Deleting {0} table entries: {1}", TableName, Utils.EnumerableToString(list));

            try
            {
                TableServiceContext svc = tableOperationsClient.GetDataServiceContext();
                foreach (var tuple in list)
                {
                    svc.AttachTo(TableName, tuple.Item1, tuple.Item2);
                    svc.DeleteObject(tuple.Item1);
                }
                try
                {
                    await Task<DataServiceResponse>.Factory.FromAsync(
                        svc.BeginSaveChangesWithRetries,
                        svc.EndSaveChangesWithRetries,
                        SaveChangesOptions.ReplaceOnUpdate | SaveChangesOptions.Batch,
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

        private List<Tuple<T, string>> PairEntitiesWithEtags(DataServiceContext svcContext, List<T> entities)
        {
            var result = new List<Tuple<T, string>>(entities.Count);
            foreach (var entity in entities)
            {
                EntityDescriptor eDesc = svcContext.GetEntityDescriptor(entity);
                string etag = eDesc.ETag;
                result.Add(Tuple.Create(entity, etag));
            }
            return result;
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
                TableServiceContext svc = tableOperationsClient.GetDataServiceContext();
                // Improve performance when table name differs from class name
                // http://www.gtrifonov.com/2011/06/15/improving-performance-for-windows-azure-tables/
                svc.ResolveType = ResolveEntityType;

                //IQueryable<T> query = svc.CreateQuery<T>(TableName).Where(predicate);
                CloudTableQuery<T> cloudTableQuery = svc.CreateQuery<T>(TableName).Where(predicate).AsTableServiceQuery(); // turn IQueryable into CloudTableQuery

                try
                {
                    Func<Task<List<T>>> executeQueryHandleContinuations = async () =>
                    {
                        // Read table with continuation token
                        // http://convective.wordpress.com/2013/11/03/queries-in-the-windows-azure-storage-client-library-v2-1/

                        // 1) First wrong sync way to read:
                        // List<T> queryResults = query.ToList(); // ToList will actually execute the query and add entities to svc. However, this will not handle continuation tokens.
                        // 2) Second correct sync way to read:
                        // http://convective.wordpress.com/2010/02/06/queries-in-azure-tables/
                        // CloudTableQuery.Execute will properly retrieve all the records from a table through the automatic handling of continuation tokens:
                        Task<ResultSegment<T>> firstSegmentPromise = Task<ResultSegment<T>>.Factory.FromAsync(
                            cloudTableQuery.BeginExecuteSegmented,
                            cloudTableQuery.EndExecuteSegmented,
                            null);
                        // 3) Third wrong async way to read:
                        // return firstSegmentPromise;
                        // 4) Forth correct async way to read - handles continuation tokens:

                        var list = new List<T>();

                        Task<ResultSegment<T>> nextSegmentAsync = firstSegmentPromise;
                        while (true)
                        {
                            ResultSegment<T> resultSegment = await nextSegmentAsync;
                            var capture = resultSegment.Results;
                            if (capture != null) // don't call Count or Any or anything else that can potentialy cause multiple evaluations of the IEnumerable
                            {
                                list.AddRange(capture);
                            }

                            if (!resultSegment.HasMoreResults)
                            {
                                // All data was read successfully if we got to here
                                break;
                            }

                            // ask to read the next segment
                            nextSegmentAsync = Task<ResultSegment<T>>.Factory.FromAsync(
                                resultSegment.BeginGetNext,
                                resultSegment.EndGetNext,
                                null);
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
                    return PairEntitiesWithEtags(svc, results);
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
                TableServiceContext svc = tableOperationsClient.GetDataServiceContext();
                foreach (T entry in data)
                {
                    svc.AttachTo(TableName, entry);
                    svc.UpdateObject(entry);
                }

                bool fallbackToInsertOneByOne = false;
                try
                {
                    // SaveChangesOptions.None == Insert-or-merge operation, SaveChangesOptions.Batch == Batch transaction
                    // http://msdn.microsoft.com/en-us/library/hh452241.aspx
                    await Task<DataServiceResponse>.Factory.FromAsync(
                        svc.BeginSaveChangesWithRetries,
                        svc.EndSaveChangesWithRetries,
                        SaveChangesOptions.None | SaveChangesOptions.Batch,
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

                    if (!fallbackToInsertOneByOne) throw;
                }

                // Bulk insert failed, so try to insert rows one by one instead
                var promises = new List<Task>();
                foreach (T entry in data)
                {
                    promises.Add(UpsertTableEntryAsync(entry));
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
                operationsClient.RetryPolicy = AzureTableDefaultPolicies.TableOperationRetryPolicy;
                operationsClient.Timeout = AzureTableDefaultPolicies.TableOperationTimeout;
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
                client.RetryPolicy = AzureTableDefaultPolicies.TableCreationRetryPolicy;
                client.Timeout = AzureTableDefaultPolicies.TableCreationTimeout;
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

            TableServiceEntity entity = new T();
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
                TableServiceContext svc = tableClient.GetDataServiceContext();
                svc.AddObject(TableName, entity);

                try
                {
                    await Task<DataServiceResponse>.Factory.FromAsync(
                        svc.BeginSaveChangesWithRetries,
                        svc.EndSaveChangesWithRetries,
                        SaveChangesOptions.None,
                        null);
                }
                catch (Exception exc)
                {
                    CheckAlertWriteError(operation + "-Create", entity, null, exc);
                    throw;
                }

                try
                {
                    svc.DeleteObject(entity);
                    await Task<DataServiceResponse>.Factory.FromAsync(
                        svc.BeginSaveChangesWithRetries,
                        svc.EndSaveChangesWithRetries,
                        SaveChangesOptions.None,
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
            if (AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus) && AzureStorageUtils.IsContentionError(httpStatusCode))
            {
                // log at Verbose, since failure on conditional is not not an error. Will analyze and warn later, if required.
                if (Logger.IsVerbose) Logger.Verbose(ErrorCode.AzureTable_13,
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


