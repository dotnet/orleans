using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Orleans.Runtime;
using OrleansSQLUtils.Storage;

namespace Orleans.SqlUtils
{
    /// <summary>
    /// A class for all relational storages that support all systems stores : membership, reminders and statistics
    /// </summary>    
    internal class RelationalOrleansQueries
    {
        /// <summary>
        /// the underlying storage
        /// </summary>
        private readonly IRelationalStorage storage;

        /// <summary>
        /// When inserting statistics and generating a batch insert clause, these are the columns in the statistics
        /// table that will be updated with multiple values. The other ones are updated with one value only.
        /// </summary>
        private readonly static string[] InsertStatisticsMultiupdateColumns =
        {
            $"@{DbStoredQueries.Columns.IsValueDelta}",
            $"@{DbStoredQueries.Columns.StatValue}",
            $"@{DbStoredQueries.Columns.Statistic}"
        };

        /// <summary>
        /// the orleans functional queries
        /// </summary>
        private readonly DbStoredQueries dbStoredQueries;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="storage">the underlying relational storage</param>
        /// <param name="dbStoredQueries">Orleans functional queries</param>
        private RelationalOrleansQueries(IRelationalStorage storage, DbStoredQueries dbStoredQueries)
        {
            this.storage = storage;
            this.dbStoredQueries = dbStoredQueries;
        }

        /// <summary>
        /// Creates an instance of a database of type <see cref="RelationalOrleansQueries"/> and Initializes Orleans queries from the database. 
        /// Orleans uses only these queries and the variables therein, nothing more.
        /// </summary>
        /// <param name="invariantName">The invariant name of the connector for this database.</param>
        /// <param name="connectionString">The connection string this database should use for database operations.</param>
        internal static async Task<RelationalOrleansQueries> CreateInstance(string invariantName, string connectionString)
        {
            var storage = RelationalStorage.CreateInstance(invariantName, connectionString);

            var queries = await storage.ReadAsync(DbStoredQueries.GetQueriesKey, DbStoredQueries.Converters.GetQueryKeyAndValue, null);

            return new RelationalOrleansQueries(storage, new DbStoredQueries(queries.ToDictionary(q => q.Key, q => q.Value)));
        }

        private Task ExecuteAsync(string query, Func<IDbCommand, DbStoredQueries.Columns> parameterProvider)
        {
            return storage.ExecuteAsync(query, command => parameterProvider(command));
        }

        private async Task<TAggregate> ReadAsync<TResult, TAggregate>(string query,
            Func<IDataRecord, TResult> selector,
            Func<IDbCommand, DbStoredQueries.Columns> parameterProvider,
            Func<IEnumerable<TResult>, TAggregate> aggregator)
        {
            var ret = await storage.ReadAsync(query, selector, command => parameterProvider(command));
            return aggregator(ret);
        }

        /// <summary>
        /// Either inserts or updates a silo metrics row.
        /// </summary>
        /// <param name="deploymentId">The deployment ID.</param>
        /// <param name="siloId">The silo ID.</param>
        /// <param name="gateway">The gateway information.</param>
        /// <param name="siloAddress">The silo address information.</param>
        /// <param name="hostName">The host name.</param>
        /// <param name="siloMetrics">The silo metrics to be either updated or inserted.</param>
        /// <returns></returns>
        internal Task UpsertSiloMetricsAsync(string deploymentId, string siloId, IPEndPoint gateway,
            SiloAddress siloAddress, string hostName, ISiloPerformanceMetrics siloMetrics)
        {
            return ExecuteAsync(dbStoredQueries.UpsertSiloMetricsKey, command =>
                new DbStoredQueries.Columns(command)
                {
                    DeploymentId = deploymentId,
                    HostName = hostName,
                    SiloMetrics = siloMetrics,
                    SiloAddress = siloAddress,
                    GatewayAddress = gateway.Address,
                    GatewayPort = gateway.Port,
                    SiloId = siloId
                });
        }

        /// <summary>
        /// Either inserts or updates a silo metrics row. 
        /// </summary>
        /// <param name="deploymentId">The deployment ID.</param>
        /// <param name="clientId">The client ID.</param>
        /// <param name="address">The client address information.</param>
        /// <param name="hostName">The hostname.</param>
        /// <param name="clientMetrics">The client metrics to be either updated or inserted.</param>
        /// <returns></returns>
        internal Task UpsertReportClientMetricsAsync(string deploymentId, string clientId, IPAddress address,
            string hostName, IClientPerformanceMetrics clientMetrics)
        {
            return ExecuteAsync(dbStoredQueries.UpsertReportClientMetricsKey, command =>
                new DbStoredQueries.Columns(command)
                {
                    DeploymentId = deploymentId,
                    HostName = hostName,
                    ClientMetrics = clientMetrics,
                    ClientId = clientId,
                    Address = address
                });
        }


        /// <summary>
        /// Inserts the given statistics counters to the Orleans database.
        /// </summary>
        /// <param name="deploymentId">The deployment ID.</param>
        /// <param name="hostName">The hostname.</param>
        /// <param name="siloOrClientName">The silo or client name.</param>
        /// <param name="id">The silo address or client ID.</param>
        /// <param name="counters">The counters to be inserted.</param>        
        internal Task InsertStatisticsCountersAsync(string deploymentId, string hostName, string siloOrClientName,
            string id, List<ICounter> counters)
        {
            var queryTemplate = dbStoredQueries.InsertOrleansStatisticsKey;
            //Zero statistic values mean either that the system is not running or no updates. Such values are not inserted and pruned
            //here so that no insert query or parameters are generated.
            counters =
                counters.Where(i => !"0".Equals(i.IsValueDelta ? i.GetDeltaString() : i.GetValueString())).ToList();
            if (counters.Count == 0)
            {
                return TaskDone.Done;
            }

            //Note that the following is almost the same as RelationalStorageExtensions.ExecuteMultipleInsertIntoAsync
            //the only difference being that some columns are skipped. Likely it would be beneficial to introduce
            //a "skip list" to RelationalStorageExtensions.ExecuteMultipleInsertIntoAsync.

            //The template contains an insert for online. The part after SELECT is copied
            //out so that certain parameters can be multiplied by their count. Effectively
            //this turns a query of type (transaction details vary by vendor)
            //BEGIN TRANSACTION; INSERT INTO [OrleansStatisticsTable] <columns> SELECT <variables>; COMMIT TRANSACTION;
            //to BEGIN TRANSACTION; INSERT INTO [OrleansStatisticsTable] <columns> SELECT <variables>; UNION ALL <variables> COMMIT TRANSACTION;
            //where the UNION ALL is multiplied as many times as there are counters to insert.
            int startFrom = queryTemplate.IndexOf("SELECT", StringComparison.Ordinal) + "SELECT".Length + 1;
                //This +1 is to have a space between SELECT and the first parameter name to not to have a SQL syntax error.
            int lastSemicolon = queryTemplate.LastIndexOf(";", StringComparison.Ordinal);
            int endTo = lastSemicolon > 0 ? queryTemplate.LastIndexOf(";", lastSemicolon - 1, StringComparison.Ordinal) : -1;
            var template = queryTemplate.Substring(startFrom, endTo - startFrom);
            var parameterNames = template.Split(new[] {","}, StringSplitOptions.RemoveEmptyEntries).Select(i => i.Trim()).ToArray();
            var collectionOfParametersToBeUnionized = new List<string>();
            var parametersToBeUnioned = new string[parameterNames.Length];
            for (int counterIndex = 0; counterIndex < counters.Count; ++counterIndex)
            {
                for (int parameterNameIndex = 0; parameterNameIndex < parameterNames.Length; ++parameterNameIndex)
                {
                    if (InsertStatisticsMultiupdateColumns.Contains(parameterNames[parameterNameIndex]))
                    {
                        //These parameters change for each row. The format is
                        //@StatValue0, @StatValue1, @StatValue2, ... @sStatValue{counters.Count}.
                        parametersToBeUnioned[parameterNameIndex] = $"{parameterNames[parameterNameIndex]}{counterIndex}";
                    }
                    else
                    {
                        //These parameters remain constant for every and each row.
                        parametersToBeUnioned[parameterNameIndex] = parameterNames[parameterNameIndex];
                    }
                }
                collectionOfParametersToBeUnionized.Add($"{string.Join(",", parametersToBeUnioned)}");
            }

            var storageConsts = DbConstantsStore.GetDbConstants(storage.InvariantName);
            var query = queryTemplate.Replace(template, string.Join(storageConsts.UnionAllSelectTemplate, collectionOfParametersToBeUnionized));
            return ExecuteAsync(query, command =>
                new DbStoredQueries.Columns(command)
                {
                    DeploymentId = deploymentId,
                    HostName = hostName,
                    Counters = counters,
                    Name = siloOrClientName,
                    Id = id
                });
        }

        /// <summary>
        /// Reads Orleans reminder data from the tables.
        /// </summary>
        /// <param name="serviceId">The service ID.</param>
        /// <param name="grainRef">The grain reference (ID).</param>
        /// <returns>Reminder table data.</returns>
        internal Task<ReminderTableData> ReadReminderRowsAsync(string serviceId, GrainReference grainRef)
        {
            return ReadAsync(dbStoredQueries.ReadReminderRowsKey, DbStoredQueries.Converters.GetReminderEntry, command =>
                new DbStoredQueries.Columns(command) {ServiceId = serviceId, GrainId = grainRef.ToKeyString()},
                ret => new ReminderTableData(ret.ToList()));
        }


        /// <summary>
        /// Reads Orleans reminder data from the tables.
        /// </summary>
        /// <param name="serviceId">The service ID.</param>
        /// <param name="beginHash">The begin hash.</param>
        /// <param name="endHash">The end hash.</param>
        /// <returns>Reminder table data.</returns>
        internal Task<ReminderTableData> ReadReminderRowsAsync(string serviceId, uint beginHash, uint endHash)
        {
            var query = (int) beginHash < (int) endHash ? dbStoredQueries.ReadRangeRows1Key : dbStoredQueries.ReadRangeRows2Key;

            return ReadAsync(query, DbStoredQueries.Converters.GetReminderEntry, command =>
                new DbStoredQueries.Columns(command) {ServiceId = serviceId, BeginHash = beginHash, EndHash = endHash},
                ret => new ReminderTableData(ret.ToList()));
        }


        /// <summary>
        /// Reads one row of reminder data.
        /// </summary>
        /// <param name="serviceId">Service ID.</param>
        /// <param name="grainRef">The grain reference (ID).</param>
        /// <param name="reminderName">The reminder name to retrieve.</param>
        /// <returns>A remainder entry.</returns>
        internal Task<ReminderEntry> ReadReminderRowAsync(string serviceId, GrainReference grainRef,
            string reminderName)
        {
            return ReadAsync(dbStoredQueries.ReadReminderRowKey, DbStoredQueries.Converters.GetReminderEntry, command =>
                new DbStoredQueries.Columns(command)
                {
                    ServiceId = serviceId,
                    GrainId = grainRef.ToKeyString(),
                    ReminderName = reminderName
                }, ret => ret.FirstOrDefault());
        }

        /// <summary>
        /// Either inserts or updates a reminder row.
        /// </summary>
        /// <param name="serviceId">The service ID.</param>
        /// <param name="grainRef">The grain reference (ID).</param>
        /// <param name="reminderName">The reminder name to retrieve.</param>
        /// <param name="startTime">Start time of the reminder.</param>
        /// <param name="period">Period of the reminder.</param>
        /// <returns>The new etag of the either or updated or inserted reminder row.</returns>
        internal Task<string> UpsertReminderRowAsync(string serviceId, GrainReference grainRef,
            string reminderName, DateTime startTime, TimeSpan period)
        {
            return ReadAsync(dbStoredQueries.UpsertReminderRowKey, DbStoredQueries.Converters.GetVersion, command =>
                new DbStoredQueries.Columns(command)
                {
                    ServiceId = serviceId,
                    GrainHash = grainRef.GetUniformHashCode(),
                    GrainId = grainRef.ToKeyString(),
                    ReminderName = reminderName,
                    StartTime = startTime,
                    Period = period
                }, ret => ret.First().ToString());
        }

        /// <summary>
        /// Deletes a reminder
        /// </summary>
        /// <param name="serviceId">Service ID.</param>
        /// <param name="grainRef"></param>
        /// <param name="reminderName"></param>
        /// <param name="etag"></param>
        /// <returns></returns>
        internal Task<bool> DeleteReminderRowAsync(string serviceId, GrainReference grainRef, string reminderName,
            string etag)
        {
            return ReadAsync(dbStoredQueries.DeleteReminderRowKey, DbStoredQueries.Converters.GetSingleBooleanValue, command =>
                new DbStoredQueries.Columns(command)
                {
                    ServiceId = serviceId,
                    GrainId = grainRef.ToKeyString(),
                    ReminderName = reminderName,
                    Version = etag
                }, ret => ret.First());
        }

        /// <summary>
        /// Deletes all reminders rows of a service id.
        /// </summary>
        /// <param name="serviceId"></param>
        /// <returns></returns>
        internal Task DeleteReminderRowsAsync(string serviceId)
        {
            return ExecuteAsync(dbStoredQueries.DeleteReminderRowsKey, command =>
                new DbStoredQueries.Columns(command) {ServiceId = serviceId});
        }

        /// <summary>
        /// Lists active gateways. Used mainly by Orleans clients.
        /// </summary>
        /// <param name="deploymentId">The deployment for which to query the gateways.</param>
        /// <returns>The gateways for the silo.</returns>
        internal Task<List<Uri>> ActiveGatewaysAsync(string deploymentId)
        {
            return ReadAsync(dbStoredQueries.GatewaysQueryKey, DbStoredQueries.Converters.GetGatewayUri, command =>
                new DbStoredQueries.Columns(command) {DeploymentId = deploymentId, Status = SiloStatus.Active},
                ret => ret.ToList());
        }

        /// <summary>
        /// Queries Orleans membership data.
        /// </summary>
        /// <param name="deploymentId">The deployment for which to query data.</param>
        /// <param name="siloAddress">Silo data used as parameters in the query.</param>
        /// <returns>Membership table data.</returns>
        internal Task<MembershipTableData> MembershipReadRowAsync(string deploymentId, SiloAddress siloAddress)
        {
            return ReadAsync(dbStoredQueries.MembershipReadRowKey, DbStoredQueries.Converters.GetMembershipEntry, command =>
                new DbStoredQueries.Columns(command) {DeploymentId = deploymentId, SiloAddress = siloAddress},
                ConvertToMembershipTableData);
        }

        /// <summary>
        /// returns all membership data for a deployment id
        /// </summary>
        /// <param name="deploymentId"></param>
        /// <returns></returns>
        internal Task<MembershipTableData> MembershipReadAllAsync(string deploymentId)
        {
            return ReadAsync(dbStoredQueries.MembershipReadAllKey, DbStoredQueries.Converters.GetMembershipEntry, command =>
                new DbStoredQueries.Columns(command) {DeploymentId = deploymentId}, ConvertToMembershipTableData);
        }

        /// <summary>
        /// deletes all membership entries for a deployment id
        /// </summary>
        /// <param name="deploymentId"></param>
        /// <returns></returns>
        internal Task DeleteMembershipTableEntriesAsync(string deploymentId)
        {
            return ExecuteAsync(dbStoredQueries.DeleteMembershipTableEntriesKey, command =>
                new DbStoredQueries.Columns(command) {DeploymentId = deploymentId});
        }

        /// <summary>
        /// Updates IAmAlive for a silo
        /// </summary>
        /// <param name="deploymentId"></param>
        /// <param name="siloAddress"></param>
        /// <param name="iAmAliveTime"></param>
        /// <returns></returns>
        internal Task UpdateIAmAliveTimeAsync(string deploymentId, SiloAddress siloAddress,DateTime iAmAliveTime)
        {
            return ExecuteAsync(dbStoredQueries.UpdateIAmAlivetimeKey, command =>
                new DbStoredQueries.Columns(command)
                {
                    DeploymentId = deploymentId,
                    SiloAddress = siloAddress,
                    IAmAliveTime = iAmAliveTime
                });
        }

        /// <summary>
        /// Inserts a version row if one does not already exist.
        /// </summary>
        /// <param name="deploymentId">The deployment for which to query data.</param>
        /// <returns><em>TRUE</em> if a row was inserted. <em>FALSE</em> otherwise.</returns>
        internal Task<bool> InsertMembershipVersionRowAsync(string deploymentId)
        {
            return ReadAsync(dbStoredQueries.InsertMembershipVersionKey, DbStoredQueries.Converters.GetSingleBooleanValue, command =>
                new DbStoredQueries.Columns(command) {DeploymentId = deploymentId}, ret => ret.First());
        }

        /// <summary>
        /// Inserts a membership row if one does not already exist.
        /// </summary>
        /// <param name="deploymentId">The deployment with which to insert row.</param>
        /// <param name="membershipEntry">The membership entry data to insert.</param>
        /// <param name="etag">The table expected version etag.</param>
        /// <returns><em>TRUE</em> if insert succeeds. <em>FALSE</em> otherwise.</returns>
        internal Task<bool> InsertMembershipRowAsync(string deploymentId, MembershipEntry membershipEntry,
            string etag)
        {
            return ReadAsync(dbStoredQueries.InsertMembershipKey, DbStoredQueries.Converters.GetSingleBooleanValue, command =>
                new DbStoredQueries.Columns(command)
                {
                    DeploymentId = deploymentId,
                    IAmAliveTime = membershipEntry.IAmAliveTime,
                    SiloName = membershipEntry.SiloName,
                    HostName = membershipEntry.HostName,
                    SiloAddress = membershipEntry.SiloAddress,
                    StartTime = membershipEntry.StartTime,
                    Status = membershipEntry.Status,
                    ProxyPort = membershipEntry.ProxyPort,
                    Version = etag
                }, ret => ret.First());
        }

        /// <summary>
        /// Updates membership row data.
        /// </summary>
        /// <param name="deploymentId">The deployment with which to insert row.</param>
        /// <param name="membershipEntry">The membership data to used to update database.</param>
        /// <param name="etag">The table expected version etag.</param>
        /// <returns><em>TRUE</em> if update SUCCEEDS. <em>FALSE</em> ot</returns>
        internal Task<bool> UpdateMembershipRowAsync(string deploymentId, MembershipEntry membershipEntry,
            string etag)
        {
            return ReadAsync(dbStoredQueries.UpdateMembershipKey, DbStoredQueries.Converters.GetSingleBooleanValue, command =>
                new DbStoredQueries.Columns(command)
                {
                    DeploymentId = deploymentId,
                    SiloAddress = membershipEntry.SiloAddress,
                    IAmAliveTime = membershipEntry.IAmAliveTime,
                    Status = membershipEntry.Status,
                    SuspectTimes = membershipEntry.SuspectTimes,
                    Version = etag
                }, ret => ret.First());
        }

        private static MembershipTableData ConvertToMembershipTableData(IEnumerable<Tuple<MembershipEntry, int>> ret)
        {
            var retList = ret.ToList();
            var tableVersionEtag = retList[0].Item2;
            var membershipEntries = new List<Tuple<MembershipEntry, string>>();
            if (retList[0].Item1 != null)
            {
                membershipEntries.AddRange(retList.Select(i => new Tuple<MembershipEntry, string>(i.Item1, string.Empty)));
            }
            return new MembershipTableData(membershipEntries, new TableVersion(tableVersionEtag, tableVersionEtag.ToString()));
        }
    }
}
