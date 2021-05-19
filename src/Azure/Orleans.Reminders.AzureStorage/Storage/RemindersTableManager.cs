using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.Logging;
using Orleans.AzureUtils.Utilities;
using Orleans.Reminders.AzureStorage;

namespace Orleans.Runtime.ReminderService
{
    internal class ReminderTableEntry : TableEntity
    {
        public string GrainReference        { get; set; }    // Part of RowKey
        public string ReminderName          { get; set; }    // Part of RowKey
        public string ServiceId             { get; set; }    // Part of PartitionKey
        public string DeploymentId          { get; set; }
        public string StartAt               { get; set; }
        public string Period                { get; set; }
        public string GrainRefConsistentHash { get; set; }    // Part of PartitionKey


        public static string ConstructRowKey(GrainReference grainRef, string reminderName)
        {
            var key = string.Format("{0}-{1}", grainRef.ToKeyString(), reminderName);
            return AzureTableUtils.SanitizeTableProperty(key);
        }

        public static (string LowerBound, string UpperBound) ConstructRowKeyBounds(GrainReference grainRef)
        {
            var baseKey = AzureTableUtils.SanitizeTableProperty(grainRef.ToKeyString());
            return (baseKey + '-', baseKey + (char)('-' + 1));
        }

        public static string ConstructPartitionKey(string serviceId, GrainReference grainRef)
        {
            return ConstructPartitionKey(serviceId, grainRef.GetUniformHashCode());
        }

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

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("Reminder [");
            sb.Append(" PartitionKey=").Append(PartitionKey);
            sb.Append(" RowKey=").Append(RowKey);

            sb.Append(" GrainReference=").Append(GrainReference);
            sb.Append(" ReminderName=").Append(ReminderName);
            sb.Append(" Deployment=").Append(DeploymentId);
            sb.Append(" ServiceId=").Append(ServiceId);
            sb.Append(" StartAt=").Append(StartAt);
            sb.Append(" Period=").Append(Period);
            sb.Append(" GrainRefConsistentHash=").Append(GrainRefConsistentHash);
            sb.Append("]");

            return sb.ToString();
        }
    }

    internal class RemindersTableManager : AzureTableDataManager<ReminderTableEntry>
    {
        public string ServiceId { get; private set; }
        public string ClusterId { get; private set; }

        public static async Task<RemindersTableManager> GetManager(string serviceId, string clusterId, ILoggerFactory loggerFactory, AzureStorageOperationOptions options)
        {
            var singleton = new RemindersTableManager(serviceId, clusterId, options, loggerFactory);
            try
            {
                singleton.Logger.Info("Creating RemindersTableManager for service id {0} and clusterId {1}.", serviceId, clusterId);
                await singleton.InitTableAsync();
            }
            catch (Exception ex)
            {
                string errorMsg = $"Exception trying to create or connect to the Azure table: {ex.Message}";
                singleton.Logger.Error((int)AzureReminderErrorCode.AzureTable_39, errorMsg, ex);
                throw new OrleansException(errorMsg, ex);
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

        internal async Task<List<Tuple<ReminderTableEntry, string>>> FindReminderEntries(uint begin, uint end)
        {
            // TODO: Determine whether or not a single query could be used here while avoiding a table scan
            string sBegin = ReminderTableEntry.ConstructPartitionKey(ServiceId, begin);
            string sEnd = ReminderTableEntry.ConstructPartitionKey(ServiceId, end);
            var (partitionKeyLowerBound, partitionKeyUpperBound) = ReminderTableEntry.ConstructPartitionKeyBounds(ServiceId);
            string filterOnServiceIdStr = TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.PartitionKey), QueryComparisons.GreaterThan, partitionKeyLowerBound),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.PartitionKey), QueryComparisons.LessThan, partitionKeyUpperBound));
            if (begin < end)
            {
                string filterBetweenBeginAndEnd = TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.PartitionKey), QueryComparisons.GreaterThan, sBegin),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.PartitionKey), QueryComparisons.LessThanOrEqual,
                        sEnd));
                string query = TableQuery.CombineFilters(filterOnServiceIdStr, TableOperators.And, filterBetweenBeginAndEnd);
                var queryResults = await ReadTableEntriesAndEtagsAsync(query);
                return queryResults.ToList();
            }

            if (begin == end)
            {
                var queryResults = await ReadTableEntriesAndEtagsAsync(filterOnServiceIdStr);
                return queryResults.ToList();
            }

            // (begin > end)
            string queryOnSBegin = TableQuery.CombineFilters(
                filterOnServiceIdStr,
                TableOperators.And,
                TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.PartitionKey), QueryComparisons.GreaterThan, sBegin));
            string queryOnSEnd = TableQuery.CombineFilters(
                filterOnServiceIdStr,
                TableOperators.And,
                TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.PartitionKey), QueryComparisons.LessThanOrEqual, sEnd));

            var resultsOnSBeginQuery = ReadTableEntriesAndEtagsAsync(queryOnSBegin);
            var resultsOnSEndQuery = ReadTableEntriesAndEtagsAsync(queryOnSEnd);
            IEnumerable<Tuple<ReminderTableEntry, string>>[] results = await Task.WhenAll(resultsOnSBeginQuery, resultsOnSEndQuery);
            return results[0].Concat(results[1]).ToList();
        }

        internal async Task<List<Tuple<ReminderTableEntry, string>>> FindReminderEntries(GrainReference grainRef)
        {
            var partitionKey = ReminderTableEntry.ConstructPartitionKey(ServiceId, grainRef);
            var (rowKeyLowerBound, rowKeyUpperBound) = ReminderTableEntry.ConstructRowKeyBounds(grainRef);
            string filter = TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.RowKey), QueryComparisons.GreaterThan, rowKeyLowerBound),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.RowKey), QueryComparisons.LessThan, rowKeyUpperBound));
            string query =
                TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.PartitionKey), QueryComparisons.Equal, partitionKey),
                    TableOperators.And,
                    filter);

            var queryResults = await ReadTableEntriesAndEtagsAsync(query);
            return queryResults.ToList();
        }

        internal async Task<Tuple<ReminderTableEntry, string>> FindReminderEntry(GrainReference grainRef, string reminderName)
        {
            string partitionKey = ReminderTableEntry.ConstructPartitionKey(ServiceId, grainRef);
            string rowKey = ReminderTableEntry.ConstructRowKey(grainRef, reminderName);

            return await ReadSingleTableEntryAsync(partitionKey, rowKey);
        }

        private Task<List<Tuple<ReminderTableEntry, string>>> FindAllReminderEntries()
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
                    if (Logger.IsEnabled(LogLevel.Trace)) Logger.Trace("UpsertRow failed with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
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
            }catch(Exception exc)
            {
                HttpStatusCode httpStatusCode;
                string restStatus;
                if (AzureTableUtils.EvaluateException(exc, out httpStatusCode, out restStatus))
                {
                    if (Logger.IsEnabled(LogLevel.Trace)) Logger.Trace("DeleteReminderEntryConditionally failed with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
                    if (AzureTableUtils.IsContentionError(httpStatusCode)) return false;
                }
                throw;
            }
        }

        internal async Task DeleteTableEntries()
        {
            List<Tuple<ReminderTableEntry, string>> entries = await FindAllReminderEntries();
            // return manager.DeleteTableEntries(entries); // this doesnt work as entries can be across partitions, which is not allowed
            // group by grain hashcode so each query goes to different partition
            var tasks = new List<Task>();
            var groupedByHash = entries
                .Where(tuple => tuple.Item1.ServiceId.Equals(ServiceId))
                .Where(tuple => tuple.Item1.DeploymentId.Equals(ClusterId))  // delete only entries that belong to our DeploymentId.
                .GroupBy(x => x.Item1.GrainRefConsistentHash).ToDictionary(g => g.Key, g => g.ToList());

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
