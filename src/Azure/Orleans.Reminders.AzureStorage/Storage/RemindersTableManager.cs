using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils.Utilities;
using Orleans.Reminders.AzureStorage;

namespace Orleans.Runtime.ReminderService
{
    internal sealed class ReminderTableEntry : ITableEntity
    {
        public string GrainReference        { get; set; }    // Part of RowKey
        public string ReminderName          { get; set; }    // Part of RowKey
        public string ServiceId             { get; set; }    // Part of PartitionKey
        public string DeploymentId          { get; set; }
        public string StartAt               { get; set; }
        public string Period                { get; set; }
        public string GrainRefConsistentHash { get; set; }    // Part of PartitionKey

        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public static string ConstructRowKey(GrainId grainId, string reminderName)
            => AzureTableUtils.SanitizeTableProperty($"{grainId}-{reminderName}");

        public static (string LowerBound, string UpperBound) ConstructRowKeyBounds(GrainId grainId)
        {
            var baseKey = AzureTableUtils.SanitizeTableProperty(grainId.ToString());
            return (baseKey + '-', baseKey + (char)('-' + 1));
        }

        public static string ConstructPartitionKey(string serviceId, GrainId grainId)
            => ConstructPartitionKey(serviceId, grainId.GetUniformHashCode());

        public static string ConstructPartitionKey(string serviceId, uint number)
        {
            // IMPORTANT NOTE: Other code using this return data is very sensitive to format changes,
            //       so take great care when making any changes here!!!

            // this format of partition key makes sure that the comparisons in FindReminderEntries(begin, end) work correctly
            // the idea is that when converting to string, negative numbers start with 0, and positive start with 1. Now,
            // when comparisons will be done on strings, this will ensure that positive numbers are always greater than negative
            // string grainHash = number < 0 ? string.Format("0{0}", number.ToString("X")) : string.Format("1{0:d16}", number);

            return AzureTableUtils.SanitizeTableProperty($"{serviceId}_{number:X8}");
        }

        public static (string LowerBound, string UpperBound) ConstructPartitionKeyBounds(string serviceId)
        {
            var baseKey = AzureTableUtils.SanitizeTableProperty(serviceId);
            return (baseKey + '_', baseKey + (char)('_' + 1));
        }

        public override string ToString() => $"Reminder [PartitionKey={PartitionKey} RowKey={RowKey} GrainId={GrainReference} ReminderName={ReminderName} Deployment={DeploymentId} ServiceId={ServiceId} StartAt={StartAt} Period={Period} GrainRefConsistentHash={GrainRefConsistentHash}]";
    }

    internal sealed class RemindersTableManager : AzureTableDataManager<ReminderTableEntry>
    {
        public string ServiceId { get; private set; }
        public string ClusterId { get; private set; }

        public static async Task<RemindersTableManager> GetManager(string serviceId, string clusterId, ILoggerFactory loggerFactory, AzureStorageOperationOptions options)
        {
            var singleton = new RemindersTableManager(serviceId, clusterId, options, loggerFactory);
            try
            {
                singleton.Logger.LogInformation("Creating RemindersTableManager for service id {ServiceId} and clusterId {ClusterId}.", serviceId, clusterId);
                await singleton.InitTableAsync();
            }
            catch (Exception ex)
            {
                singleton.Logger.LogError((int)AzureReminderErrorCode.AzureTable_39, ex, "Exception trying to create or connect to the Azure table");
                throw new OrleansException("Exception trying to create or connect to the Azure table", ex);
            }
            return singleton;
        }
        
        private RemindersTableManager(
            string serviceId,
            string clusterId,
            AzureStorageOperationOptions options,
            ILoggerFactory loggerFactory)
            : base(options, loggerFactory.CreateLogger<RemindersTableManager>())
        {
            ClusterId = clusterId;
            ServiceId = serviceId;
        }

        internal async Task<List<(ReminderTableEntry Entity, string ETag)>> FindReminderEntries(uint begin, uint end)
        {
            string sBegin = ReminderTableEntry.ConstructPartitionKey(ServiceId, begin);
            string sEnd = ReminderTableEntry.ConstructPartitionKey(ServiceId, end);
            string query;
            if (begin < end)
            {
                // Query between the specified lower and upper bounds.
                // Note that the lower bound is exclusive and the upper bound is inclusive in the below query.
                query = TableClient.CreateQueryFilter($"(PartitionKey gt {sBegin}) and (PartitionKey le {sEnd})");
            }
            else
            {
                var (partitionKeyLowerBound, partitionKeyUpperBound) = ReminderTableEntry.ConstructPartitionKeyBounds(ServiceId);
                if (begin == end)
                {
                    // Query the entire range
                    query = TableClient.CreateQueryFilter($"(PartitionKey gt {partitionKeyLowerBound}) and (PartitionKey lt {partitionKeyUpperBound})");
                }
                else
                {
                    // (begin > end)
                    // Query wraps around the ends of the range, so the query is the union of two disjunct queries
                    // Include everything outside of the (begin, end] range, which wraps around to become:
                    // [partitionKeyLowerBound, end] OR (begin, partitionKeyUpperBound]
                    Debug.Assert(begin > end);
                    query = TableClient.CreateQueryFilter($"((PartitionKey gt {partitionKeyLowerBound}) and (PartitionKey le {sEnd})) or ((PartitionKey gt {sBegin}) and (PartitionKey lt {partitionKeyUpperBound}))");
                }
            }

            var queryResults = await ReadTableEntriesAndEtagsAsync(query);
            return queryResults.ToList();
        }

        internal async Task<List<(ReminderTableEntry Entity, string ETag)>> FindReminderEntries(GrainId grainId)
        {
            var partitionKey = ReminderTableEntry.ConstructPartitionKey(ServiceId, grainId);
            var (rowKeyLowerBound, rowKeyUpperBound) = ReminderTableEntry.ConstructRowKeyBounds(grainId);
            var query = TableClient.CreateQueryFilter($"(PartitionKey eq {partitionKey}) and ((RowKey gt {rowKeyLowerBound}) and (RowKey le {rowKeyUpperBound}))");
            var queryResults = await ReadTableEntriesAndEtagsAsync(query);
            return queryResults.ToList();
        }

        internal async Task<(ReminderTableEntry Entity, string ETag)> FindReminderEntry(GrainId grainId, string reminderName)
        {
            string partitionKey = ReminderTableEntry.ConstructPartitionKey(ServiceId, grainId);
            string rowKey = ReminderTableEntry.ConstructRowKey(grainId, reminderName);

            return await ReadSingleTableEntryAsync(partitionKey, rowKey);
        }

        private Task<List<(ReminderTableEntry Entity, string ETag)>> FindAllReminderEntries()
        {
            return FindReminderEntries(0, 0);
        }

        internal async Task<string> UpsertRow(ReminderTableEntry reminderEntry)
        {
            try
            {
                return await UpsertTableEntryAsync(reminderEntry);
            }
            catch(Exception exc)
            {
                HttpStatusCode httpStatusCode;
                string restStatus;
                if (AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus))
                {
                    if (Logger.IsEnabled(LogLevel.Trace)) Logger.LogTrace("UpsertRow failed with HTTP status code: {HttpStatusCode}, REST status: {RestStatus}", httpStatusCode, restStatus);
                    if (AzureTableUtils.IsContentionError(httpStatusCode)) return null; // false;
                }
                throw;
            }
        }


        internal async Task<bool> DeleteReminderEntryConditionally(ReminderTableEntry reminderEntry, string eTag)
        {
            try
            {
                await DeleteTableEntryAsync(reminderEntry, eTag);
                return true;
            }
            catch(Exception exc)
            {
                HttpStatusCode httpStatusCode;
                string restStatus;
                if (AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus))
                {
                    if (Logger.IsEnabled(LogLevel.Trace))
                        Logger.LogTrace(
                            "DeleteReminderEntryConditionally failed with HTTP status code: {HttpStatusCode}, REST status: {RestStatus}",
                            httpStatusCode,
                            restStatus);
                    if (AzureTableUtils.IsContentionError(httpStatusCode)) return false;
                }
                throw;
            }
        }

        internal async Task DeleteTableEntries()
        {
            List<(ReminderTableEntry Entity, string ETag)> entries = await FindAllReminderEntries();
            // return manager.DeleteTableEntries(entries); // this doesnt work as entries can be across partitions, which is not allowed
            // group by grain hashcode so each query goes to different partition
            var tasks = new List<Task>();
            var groupedByHash = entries
                .Where(tuple => tuple.Entity.ServiceId.Equals(ServiceId))
                .Where(tuple => tuple.Entity.DeploymentId.Equals(ClusterId))  // delete only entries that belong to our DeploymentId.
                .GroupBy(x => x.Entity.GrainRefConsistentHash).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var entriesPerPartition in groupedByHash.Values)
            {
                    foreach (var batch in entriesPerPartition.BatchIEnumerable(this.StoragePolicyOptions.MaxBulkUpdateRows))
                {
                    tasks.Add(DeleteTableEntriesAsync(batch));
                }
            }

            await Task.WhenAll(tasks);
        }
    }
}
