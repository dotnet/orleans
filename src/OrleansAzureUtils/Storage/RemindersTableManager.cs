using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans.AzureUtils;


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
            var key = String.Format("{0}-{1}", grainRef.ToKeyString(), reminderName); //grainRef.ToString(), reminderName);
            return AzureStorageUtils.SanitizeTableProperty(key);
        }

        public static string ConstructPartitionKey(Guid serviceId, GrainReference grainRef)
        {
            return ConstructPartitionKey(serviceId, grainRef.GetUniformHashCode());
        }

        public static string ConstructPartitionKey(Guid serviceId, uint number)
        {
            // IMPORTANT NOTE: Other code using this return data is very sensitive to format changes, 
            //       so take great care when making any changes here!!!

            // this format of partition key makes sure that the comparisons in FindReminderEntries(begin, end) work correctly
            // the idea is that when converting to string, negative numbers start with 0, and positive start with 1. Now,
            // when comparisons will be done on strings, this will ensure that positive numbers are always greater than negative
            // string grainHash = number < 0 ? string.Format("0{0}", number.ToString("X")) : string.Format("1{0:d16}", number);

            var grainHash = String.Format("{0:X8}", number);
            return String.Format("{0}_{1}", ConstructServiceIdStr(serviceId), grainHash);
        }

        public static string ConstructServiceIdStr(Guid serviceId)
        {
            return serviceId.ToString();
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
        private const string REMINDERS_TABLE_NAME = "OrleansReminders";

        public Guid ServiceId { get; private set; }
        public string DeploymentId { get; private set; }

        private static readonly TimeSpan initTimeout = AzureTableDefaultPolicies.TableCreationTimeout;

        public static async Task<RemindersTableManager> GetManager(Guid serviceId, string deploymentId, string storageConnectionString)
        {
            var singleton = new RemindersTableManager(serviceId, deploymentId, storageConnectionString);
            try
            {
                singleton.Logger.Info("Creating RemindersTableManager for service id {0} and deploymentId {1}.", serviceId, deploymentId);
                await singleton.InitTableAsync()
                    .WithTimeout(initTimeout);
            }
            catch (TimeoutException te)
            {
                string errorMsg = $"Unable to create or connect to the Azure table in {initTimeout}";
                singleton.Logger.Error(ErrorCode.AzureTable_38, errorMsg, te);
                throw new OrleansException(errorMsg, te);
            }
            catch (Exception ex)
            {
                string errorMsg = $"Exception trying to create or connect to the Azure table: {ex.Message}";
                singleton.Logger.Error(ErrorCode.AzureTable_39, errorMsg, ex);
                throw new OrleansException(errorMsg, ex);
            }
            return singleton;
        }

        private RemindersTableManager(Guid serviceId, string deploymentId, string storageConnectionString)
            : base(REMINDERS_TABLE_NAME, storageConnectionString)
        {
            DeploymentId = deploymentId;
            ServiceId = serviceId;
        }

        internal async Task<List<Tuple<ReminderTableEntry, string>>> FindReminderEntries(uint begin, uint end)
        {
            // TODO: Determine whether or not a single query could be used here while avoiding a table scan
            string sBegin = ReminderTableEntry.ConstructPartitionKey(ServiceId, begin);
            string sEnd = ReminderTableEntry.ConstructPartitionKey(ServiceId, end);
            string serviceIdStr = ReminderTableEntry.ConstructServiceIdStr(ServiceId);
            string filterOnServiceIdStr = TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.PartitionKey), QueryComparisons.GreaterThan, serviceIdStr + '_'),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.PartitionKey), QueryComparisons.LessThanOrEqual,
                        serviceIdStr + (char)('_' + 1)));
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
                filterOnServiceIdStr, TableOperators.And, TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.PartitionKey), QueryComparisons.GreaterThan, sBegin));
            string queryOnSEnd = TableQuery.CombineFilters(
                filterOnServiceIdStr, TableOperators.And, TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.PartitionKey), QueryComparisons.LessThanOrEqual, sEnd));

            var resultsOnSBeginQuery = ReadTableEntriesAndEtagsAsync(queryOnSBegin);
            var resultsOnSEndQuery = ReadTableEntriesAndEtagsAsync(queryOnSEnd);
            IEnumerable<Tuple<ReminderTableEntry, string>>[] results = await Task.WhenAll(resultsOnSBeginQuery, resultsOnSEndQuery);
            return results[0].Concat(results[1]).ToList();
        }

        internal async Task<List<Tuple<ReminderTableEntry, string>>> FindReminderEntries(GrainReference grainRef)
        {
            var partitionKey = ReminderTableEntry.ConstructPartitionKey(ServiceId, grainRef);
            string filter = TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.RowKey), QueryComparisons.GreaterThan, grainRef.ToKeyString() + '-'),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition(nameof(ReminderTableEntry.RowKey), QueryComparisons.LessThanOrEqual,
                       grainRef.ToKeyString() + (char)('-' + 1)));
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
                if (AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus))
                {
                    if (Logger.IsVerbose2) Logger.Verbose2("UpsertRow failed with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
                    if (AzureStorageUtils.IsContentionError(httpStatusCode)) return null; // false;
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
                if (AzureStorageUtils.EvaluateException(exc, out httpStatusCode, out restStatus))
                {
                    if (Logger.IsVerbose2) Logger.Verbose2("DeleteReminderEntryConditionally failed with httpStatusCode={0}, restStatus={1}", httpStatusCode, restStatus);
                    if (AzureStorageUtils.IsContentionError(httpStatusCode)) return false;
                }
                throw;
            }
        }

        #region Table operations

        internal async Task DeleteTableEntries()
        {
            if (ServiceId.Equals(Guid.Empty) && DeploymentId == null)
            {
                await DeleteTableAsync();
            }
            else
            {
                List<Tuple<ReminderTableEntry, string>> entries = await FindAllReminderEntries();
                // return manager.DeleteTableEntries(entries); // this doesnt work as entries can be across partitions, which is not allowed
                // group by grain hashcode so each query goes to different partition
                var tasks = new List<Task>();
                var groupedByHash = entries
                    .Where(tuple => tuple.Item1.ServiceId.Equals(ReminderTableEntry.ConstructServiceIdStr(ServiceId)))
                    .Where(tuple => tuple.Item1.DeploymentId.Equals(DeploymentId))  // delete only entries that belong to our DeploymentId.
                    .GroupBy(x => x.Item1.GrainRefConsistentHash).ToDictionary(g => g.Key, g => g.ToList());

                foreach (var entriesPerPartition in groupedByHash.Values)
                {
                    foreach (var batch in entriesPerPartition.BatchIEnumerable(AzureTableDefaultPolicies.MAX_BULK_UPDATE_ROWS))
                    {
                        tasks.Add(DeleteTableEntriesAsync(batch));
                    }
                }

                await Task.WhenAll(tasks);
            }
        }

        #endregion
    }
}
