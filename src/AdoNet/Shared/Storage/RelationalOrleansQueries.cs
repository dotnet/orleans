using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

#if CLUSTERING_ADONET
namespace Orleans.Clustering.AdoNet.Storage
#elif PERSISTENCE_ADONET
namespace Orleans.Persistence.AdoNet.Storage
#elif REMINDERS_ADONET
namespace Orleans.Reminders.AdoNet.Storage
#elif STREAMING_ADONET
namespace Orleans.Streaming.AdoNet.Storage
#elif GRAINDIRECTORY_ADONET
namespace Orleans.GrainDirectory.AdoNet.Storage
#elif TESTER_SQLUTILS
using Orleans.Streaming.AdoNet;
using Orleans.GrainDirectory.AdoNet;
namespace Orleans.Tests.SqlUtils
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
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
        private static readonly string[] InsertStatisticsMultiupdateColumns = {
            DbStoredQueries.Columns.IsValueDelta,
            DbStoredQueries.Columns.StatValue,
            DbStoredQueries.Columns.Statistic
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
        internal RelationalOrleansQueries(IRelationalStorage storage, DbStoredQueries dbStoredQueries)
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
        internal static Task<RelationalOrleansQueries> CreateInstance(string invariantName, string connectionString) =>
            CreateInstance(RelationalStorage.CreateInstance(invariantName, connectionString));

        /// <summary>
        /// Creates an instance using exactly one configured connection source and initializes Orleans queries from the database.
        /// </summary>
        internal static Task<RelationalOrleansQueries> CreateInstance(string invariantName, string? connectionString, DbDataSource? dataSource) =>
            CreateInstance(RelationalStorage.CreateInstance(invariantName, connectionString, dataSource));

        internal static async Task<RelationalOrleansQueries> CreateInstance(IRelationalStorage storage)
        {
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
            Func<IEnumerable<TResult>, TAggregate> aggregator,
            CancellationToken cancellationToken = default)
        {
            var ret = await storage.ReadAsync(
                query,
                selector,
                command => parameterProvider(command),
                cancellationToken);
            return aggregator(ret);
        }

#if REMINDERS_ADONET || TESTER_SQLUTILS

        /// <summary>
        /// Reads Orleans reminder data from the tables.
        /// </summary>
        /// <param name="serviceId">The service ID.</param>
        /// <param name="grainId">The grain reference (ID).</param>
        /// <returns>Reminder table data.</returns>
        internal Task<ReminderTableData> ReadReminderRowsAsync(string serviceId, GrainId grainId)
        {
            // Collection queries only yield rows containing reminder data; the selector is nullable for single-row outer joins.
            return ReadAsync(dbStoredQueries.ReadReminderRowsKey, record => GetReminderEntry(record)!, command =>
                new DbStoredQueries.Columns(command) { ServiceId = serviceId, GrainId = grainId.ToString() },
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
            var query = IsReminderRangeNonWrappingInSignedOrder(beginHash, endHash)
                ? dbStoredQueries.ReadRangeRows1Key
                : dbStoredQueries.ReadRangeRows2Key;

            // Collection queries only yield rows containing reminder data; the selector is nullable for single-row outer joins.
            return ReadAsync<ReminderEntry, ReminderTableData>(query, record => GetReminderEntry(record)!, command =>
                new DbStoredQueries.Columns(command) { ServiceId = serviceId, BeginHash = beginHash, EndHash = endHash },
                ret => new ReminderTableData(ret.ToList()));
        }

        internal static bool IsReminderRangeNonWrappingInSignedOrder(uint beginHash, uint endHash)
            => unchecked((int)beginHash) < unchecked((int)endHash);

        internal static KeyValuePair<string, string> GetQueryKeyAndValue(IDataRecord record)
        {
            return new KeyValuePair<string, string>(record.GetValue<string>("QueryKey"),
                record.GetValue<string>("QueryText"));
        }

        internal static ReminderEntry? GetReminderEntry(IDataRecord record)
        {
            //Having non-null field, GrainId, means with the query filter options, an entry was found.
            string? grainId = record.GetValueOrDefault<string>(nameof(DbStoredQueries.Columns.GrainId));
            if (grainId != null)
            {
                return new ReminderEntry
                {
                    GrainId = GrainId.Parse(grainId),
                    ReminderName = record.GetValue<string>(nameof(DbStoredQueries.Columns.ReminderName)),
                    StartAt = record.GetDateTimeValue(nameof(DbStoredQueries.Columns.StartTime)),

                    //Use the GetInt64 method instead of the generic GetValue<TValue> version to retrieve the value from the data record
                    //GetValue<int> causes an InvalidCastException with oracle data provider. See https://github.com/dotnet/orleans/issues/3561
                    Period = TimeSpan.FromMilliseconds(record.GetInt64(nameof(DbStoredQueries.Columns.Period))),
                    ETag = DbStoredQueries.Converters.GetVersion(record).ToString()
                };
            }
            return null;
        }
        /// <summary>
        /// Reads one row of reminder data.
        /// </summary>
        /// <param name="serviceId">Service ID.</param>
        /// <param name="grainId">The grain reference (ID).</param>
        /// <param name="reminderName">The reminder name to retrieve.</param>
        /// <returns>A remainder entry.</returns>
        internal Task<ReminderEntry?> ReadReminderRowAsync(string serviceId, GrainId grainId,
            string reminderName)
        {
            return ReadAsync(dbStoredQueries.ReadReminderRowKey, GetReminderEntry, command =>
                new DbStoredQueries.Columns(command)
                {
                    ServiceId = serviceId,
                    GrainId = grainId.ToString(),
                    ReminderName = reminderName
                }, ret => ret.FirstOrDefault());
        }

        /// <summary>
        /// Either inserts or updates a reminder row.
        /// </summary>
        /// <param name="serviceId">The service ID.</param>
        /// <param name="grainId">The grain reference (ID).</param>
        /// <param name="reminderName">The reminder name to retrieve.</param>
        /// <param name="startTime">Start time of the reminder.</param>
        /// <param name="period">Period of the reminder.</param>
        /// <returns>The new etag of the either or updated or inserted reminder row.</returns>
        internal Task<string?> UpsertReminderRowAsync(string serviceId, GrainId grainId,
            string reminderName, DateTime startTime, TimeSpan period)
        {
            return ReadAsync<int, string?>(dbStoredQueries.UpsertReminderRowKey, DbStoredQueries.Converters.GetVersion, command =>
                new DbStoredQueries.Columns(command)
                {
                    ServiceId = serviceId,
                    GrainHash = grainId.GetUniformHashCode(),
                    GrainId = grainId.ToString(),
                    ReminderName = reminderName,
                    StartTime = startTime,
                    Period = period
                }, ret => ret.First().ToString());
        }

        /// <summary>
        /// Deletes a reminder
        /// </summary>
        /// <param name="serviceId">Service ID.</param>
        /// <param name="grainId"></param>
        /// <param name="reminderName"></param>
        /// <param name="etag"></param>
        /// <returns></returns>
        internal Task<bool> DeleteReminderRowAsync(string serviceId, GrainId grainId, string reminderName,
            string etag)
        {
            return ReadAsync(dbStoredQueries.DeleteReminderRowKey, DbStoredQueries.Converters.GetSingleBooleanValue, command =>
                new DbStoredQueries.Columns(command)
                {
                    ServiceId = serviceId,
                    GrainId = grainId.ToString(),
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
                new DbStoredQueries.Columns(command) { ServiceId = serviceId });
        }

#endif

#if CLUSTERING_ADONET || TESTER_SQLUTILS

        /// <summary>
        /// Lists active gateways. Used mainly by Orleans clients.
        /// </summary>
        /// <param name="deploymentId">The deployment for which to query the gateways.</param>
        /// <returns>The gateways for the silo.</returns>
        internal Task<List<Uri>> ActiveGatewaysAsync(string deploymentId)
        {
            return ReadAsync(dbStoredQueries.GatewaysQueryKey, DbStoredQueries.Converters.GetGatewayUri, command =>
                new DbStoredQueries.Columns(command) { DeploymentId = deploymentId, Status = SiloStatus.Active },
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
                new DbStoredQueries.Columns(command) { DeploymentId = deploymentId, SiloAddress = siloAddress },
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
                new DbStoredQueries.Columns(command) { DeploymentId = deploymentId }, ConvertToMembershipTableData);
        }

        /// <summary>
        /// deletes all membership entries for a deployment id
        /// </summary>
        /// <param name="deploymentId"></param>
        /// <returns></returns>
        internal Task DeleteMembershipTableEntriesAsync(string deploymentId)
        {
            return ExecuteAsync(dbStoredQueries.DeleteMembershipTableEntriesKey, command =>
                new DbStoredQueries.Columns(command) { DeploymentId = deploymentId });
        }

        /// <summary>
        /// deletes all membership entries for inactive silos where the IAmAliveTime is before the beforeDate parameter
        /// and the silo status is <seealso cref="SiloStatus.Dead"/>.
        /// </summary>
        /// <param name="beforeDate"></param>
        /// <param name="deploymentId"></param>
        /// <returns></returns>
        internal Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, string deploymentId)
        {
            return ExecuteAsync(dbStoredQueries.CleanupDefunctSiloEntriesKey, command =>
                new DbStoredQueries.Columns(command) { DeploymentId = deploymentId, IAmAliveTime = beforeDate.UtcDateTime });
        }

        /// <summary>
        /// Updates IAmAlive for a silo
        /// </summary>
        /// <param name="deploymentId"></param>
        /// <param name="siloAddress"></param>
        /// <param name="iAmAliveTime"></param>
        /// <returns></returns>
        internal Task UpdateIAmAliveTimeAsync(string deploymentId, SiloAddress siloAddress, DateTime iAmAliveTime)
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
                new DbStoredQueries.Columns(command) { DeploymentId = deploymentId }, ret => ret.First());
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

        private static MembershipTableData ConvertToMembershipTableData(IEnumerable<Tuple<MembershipEntry?, int>> ret)
        {
            var retList = ret.ToList();
            var tableVersionEtag = retList[0].Item2;
            var membershipEntries = new List<Tuple<MembershipEntry, string>>();
            if (retList[0].Item1 != null)
            {
                membershipEntries.AddRange(retList.Select(i => new Tuple<MembershipEntry, string>(i.Item1!, string.Empty)));
            }
            return new MembershipTableData(membershipEntries, new TableVersion(tableVersionEtag, tableVersionEtag.ToString()));
        }

#endif

#if STREAMING_ADONET || TESTER_SQLUTILS

        /// <summary>
        /// Appends an immutable stream record to a stream partition.
        /// </summary>
        /// <param name="serviceId">The service identifier.</param>
        /// <param name="providerId">The provider identifier.</param>
        /// <param name="queueId">The queue identifier.</param>
        /// <param name="streamIdBytes">The canonical <see cref="StreamId.FullKey"/> bytes.</param>
        /// <param name="streamNamespaceLength">The namespace boundary within <paramref name="streamIdBytes"/>.</param>
        /// <param name="payload">The serialized event payload.</param>
        /// <returns>An acknowledgement containing the allocated partition-local message identifier.</returns>
        internal Task<AdoNetStreamMessageAck> AppendStreamMessageAsync(
            string serviceId,
            string providerId,
            string queueId,
            byte[] streamIdBytes,
            int streamNamespaceLength,
            byte[] payload)
        {
            ArgumentNullException.ThrowIfNull(serviceId);
            ArgumentNullException.ThrowIfNull(providerId);
            ArgumentNullException.ThrowIfNull(queueId);
            ArgumentNullException.ThrowIfNull(streamIdBytes);
            ArgumentNullException.ThrowIfNull(payload);
            if (streamIdBytes.Length == 0 || streamNamespaceLength < 0 || streamNamespaceLength >= streamIdBytes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(streamNamespaceLength), "The stream namespace boundary must leave a non-empty stream key.");
            }

            return ReadAsync(
                dbStoredQueries.AppendStreamMessageKey,
                record => new AdoNetStreamMessageAck(
                    (string)record[nameof(AdoNetStreamMessageAck.ServiceId)],
                    (string)record[nameof(AdoNetStreamMessageAck.ProviderId)],
                    (string)record[nameof(AdoNetStreamMessageAck.QueueId)],
                    (long)record[nameof(AdoNetStreamMessageAck.MessageId)]),
                command => new DbStoredQueries.Columns(command)
                {
                    ServiceId = serviceId,
                    ProviderId = providerId,
                    QueueId = queueId,
                    StreamIdBytes = streamIdBytes,
                    StreamNamespaceLength = streamNamespaceLength,
                    Payload = payload
                },
                result => result.Single());
        }

        /// <summary>
        /// Acquires partition ownership and initializes its checkpoint when necessary.
        /// </summary>
        internal Task<AdoNetStreamPartitionState> AcquireStreamPartitionAsync(
            string serviceId,
            string providerId,
            string queueId,
            bool startFromNow,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(serviceId);
            ArgumentNullException.ThrowIfNull(providerId);
            ArgumentNullException.ThrowIfNull(queueId);

            return ReadAsync(
                dbStoredQueries.AcquireStreamPartitionKey,
                record => new AdoNetStreamPartitionState(
                    (string)record[nameof(AdoNetStreamPartitionState.ServiceId)],
                    (string)record[nameof(AdoNetStreamPartitionState.ProviderId)],
                    (string)record[nameof(AdoNetStreamPartitionState.QueueId)],
                    (long)record[nameof(AdoNetStreamPartitionState.OwnerEpoch)],
                    (long)record[nameof(AdoNetStreamPartitionState.NextMessageId)],
                    GetNullableInt64(record, nameof(AdoNetStreamPartitionState.Checkpoint)),
                    GetNullableInt64(record, nameof(AdoNetStreamPartitionState.EarliestMessageId)),
                    GetNullableInt64(record, nameof(AdoNetStreamPartitionState.TailMessageId))),
                command => new DbStoredQueries.Columns(command)
                {
                    ServiceId = serviceId,
                    ProviderId = providerId,
                    QueueId = queueId,
                    StartFromNow = startFromNow
                },
                result => result.Single(),
                cancellationToken);
        }

        /// <summary>
        /// Reads retained stream records with identifiers strictly greater than <paramref name="afterMessageId"/>.
        /// </summary>
        internal Task<IList<AdoNetStreamMessage>> ReadStreamMessagesAsync(
            string serviceId,
            string providerId,
            string queueId,
            long afterMessageId,
            int maxCount,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(serviceId);
            ArgumentNullException.ThrowIfNull(providerId);
            ArgumentNullException.ThrowIfNull(queueId);
            ArgumentOutOfRangeException.ThrowIfNegative(afterMessageId);
            ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);

            return ReadAsync<AdoNetStreamMessage, IList<AdoNetStreamMessage>>(
                dbStoredQueries.ReadStreamMessagesKey,
                record => new AdoNetStreamMessage(
                    (string)record[nameof(AdoNetStreamMessage.ServiceId)],
                    (string)record[nameof(AdoNetStreamMessage.ProviderId)],
                    (string)record[nameof(AdoNetStreamMessage.QueueId)],
                    (long)record[nameof(AdoNetStreamMessage.MessageId)],
                    (byte[])record[nameof(AdoNetStreamMessage.StreamIdBytes)],
                    (int)record[nameof(AdoNetStreamMessage.StreamNamespaceLength)],
                    record.GetDateTimeValue(nameof(AdoNetStreamMessage.CreatedOn)),
                    (byte[])record[nameof(AdoNetStreamMessage.Payload)]),
                command => new DbStoredQueries.Columns(command)
                {
                    ServiceId = serviceId,
                    ProviderId = providerId,
                    QueueId = queueId,
                    AfterMessageId = afterMessageId,
                    MaxCount = maxCount
                },
                result => result.ToList(),
                cancellationToken);
        }

        /// <summary>
        /// Advances a checkpoint only when the caller owns the current epoch and the value moves forward.
        /// </summary>
        internal Task<AdoNetStreamCheckpointUpdate?> AdvanceStreamCheckpointAsync(
            string serviceId,
            string providerId,
            string queueId,
            long ownerEpoch,
            long checkpoint,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(serviceId);
            ArgumentNullException.ThrowIfNull(providerId);
            ArgumentNullException.ThrowIfNull(queueId);
            ArgumentOutOfRangeException.ThrowIfLessThan(ownerEpoch, 1);
            ArgumentOutOfRangeException.ThrowIfNegative(checkpoint);

            return ReadAsync<AdoNetStreamCheckpointUpdate, AdoNetStreamCheckpointUpdate?>(
                dbStoredQueries.AdvanceStreamCheckpointKey,
                record => new AdoNetStreamCheckpointUpdate(
                    (string)record[nameof(AdoNetStreamCheckpointUpdate.ServiceId)],
                    (string)record[nameof(AdoNetStreamCheckpointUpdate.ProviderId)],
                    (string)record[nameof(AdoNetStreamCheckpointUpdate.QueueId)],
                    (long)record[nameof(AdoNetStreamCheckpointUpdate.OwnerEpoch)],
                    GetNullableInt64(record, nameof(AdoNetStreamCheckpointUpdate.Checkpoint)),
                    Convert.ToBoolean(record[nameof(AdoNetStreamCheckpointUpdate.Updated)])),
                command => new DbStoredQueries.Columns(command)
                {
                    ServiceId = serviceId,
                    ProviderId = providerId,
                    QueueId = queueId,
                    OwnerEpoch = ownerEpoch,
                    Checkpoint = checkpoint
                },
                result => result.SingleOrDefault(),
                cancellationToken);
        }

        /// <summary>
        /// Reads the current checkpoint, ownership epoch, and retained bounds of partition history.
        /// </summary>
        internal Task<AdoNetStreamPartitionState?> GetStreamPartitionBoundsAsync(
            string serviceId,
            string providerId,
            string queueId)
        {
            ArgumentNullException.ThrowIfNull(serviceId);
            ArgumentNullException.ThrowIfNull(providerId);
            ArgumentNullException.ThrowIfNull(queueId);

            return ReadAsync<AdoNetStreamPartitionState, AdoNetStreamPartitionState?>(
                dbStoredQueries.GetStreamPartitionBoundsKey,
                record => new AdoNetStreamPartitionState(
                    (string)record[nameof(AdoNetStreamPartitionState.ServiceId)],
                    (string)record[nameof(AdoNetStreamPartitionState.ProviderId)],
                    (string)record[nameof(AdoNetStreamPartitionState.QueueId)],
                    (long)record[nameof(AdoNetStreamPartitionState.OwnerEpoch)],
                    (long)record[nameof(AdoNetStreamPartitionState.NextMessageId)],
                    GetNullableInt64(record, nameof(AdoNetStreamPartitionState.Checkpoint)),
                    GetNullableInt64(record, nameof(AdoNetStreamPartitionState.EarliestMessageId)),
                    GetNullableInt64(record, nameof(AdoNetStreamPartitionState.TailMessageId))),
                command => new DbStoredQueries.Columns(command)
                {
                    ServiceId = serviceId,
                    ProviderId = providerId,
                    QueueId = queueId
                },
                result => result.SingleOrDefault());
        }

        /// <summary>
        /// Deletes an ordered, bounded batch of retained stream records.
        /// </summary>
        internal Task<AdoNetStreamCleanupResult> CleanupStreamMessagesAsync(
            string serviceId,
            string providerId,
            string queueId,
            int retentionPeriodSeconds,
            int? maximumRetentionPeriodSeconds,
            int cleanupIntervalSeconds,
            int cleanupBatchSize,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(serviceId);
            ArgumentNullException.ThrowIfNull(providerId);
            ArgumentNullException.ThrowIfNull(queueId);
            ArgumentOutOfRangeException.ThrowIfLessThan(retentionPeriodSeconds, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(cleanupIntervalSeconds, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(cleanupBatchSize, 1);
            if (maximumRetentionPeriodSeconds is { } maximum && maximum < retentionPeriodSeconds)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumRetentionPeriodSeconds), "The maximum retention period must be greater than or equal to the normal retention period.");
            }

            return ReadAsync(
                dbStoredQueries.CleanupStreamMessagesKey,
                record => new AdoNetStreamCleanupResult(
                    Convert.ToBoolean(record[nameof(AdoNetStreamCleanupResult.Ran)]),
                    Convert.ToInt32(record[nameof(AdoNetStreamCleanupResult.DeletedCount)]),
                    GetNullableInt64(record, nameof(AdoNetStreamCleanupResult.DeletedThroughMessageId)),
                    Convert.ToInt32(record[nameof(AdoNetStreamCleanupResult.HardDeletedCount)]),
                    GetNullableInt64(record, nameof(AdoNetStreamCleanupResult.HardDeletedFromMessageId)),
                    GetNullableInt64(record, nameof(AdoNetStreamCleanupResult.HardDeletedThroughMessageId)),
                    GetNullableInt64(record, nameof(AdoNetStreamCleanupResult.Checkpoint)),
                    GetNullableInt64(record, nameof(AdoNetStreamCleanupResult.EarliestMessageId)),
                    GetNullableInt64(record, nameof(AdoNetStreamCleanupResult.TailMessageId))),
                command => new DbStoredQueries.Columns(command)
                {
                    ServiceId = serviceId,
                    ProviderId = providerId,
                    QueueId = queueId,
                    RetentionPeriodSeconds = retentionPeriodSeconds,
                    MaximumRetentionPeriodSeconds = maximumRetentionPeriodSeconds,
                    CleanupIntervalSeconds = cleanupIntervalSeconds,
                    CleanupBatchSize = cleanupBatchSize
                },
                result => result.Single(),
                cancellationToken);
        }

        private static long? GetNullableInt64(IDataRecord record, string fieldName)
        {
            var ordinal = record.GetOrdinal(fieldName);
            return record.IsDBNull(ordinal) ? null : Convert.ToInt64(record.GetValue(ordinal));
        }

#endif

#if GRAINDIRECTORY_ADONET || TESTER_SQLUTILS

        /// <summary>
        /// Registers a new grain activation.
        /// </summary>
        /// <param name="clusterId">The cluster identifier.</param>
        /// <param name="providerId">The grain directory provider identifier.</param>
        /// <param name="grainId">The grain identifier.</param>
        /// <param name="siloAddress">The silo address.</param>
        /// <param name="activationId">The activation identifier.</param>
        /// <returns>The grain activation registered in the directory.</returns>
        internal Task<AdoNetGrainDirectoryEntry> RegisterGrainActivationAsync(string clusterId, string providerId, string grainId, string siloAddress, string activationId)
        {
            ArgumentNullException.ThrowIfNull(clusterId);
            ArgumentNullException.ThrowIfNull(providerId);
            ArgumentNullException.ThrowIfNull(grainId);
            ArgumentNullException.ThrowIfNull(siloAddress);
            ArgumentNullException.ThrowIfNull(activationId);

            return ReadAsync(
                dbStoredQueries.RegisterGrainActivationKey,
                record => new AdoNetGrainDirectoryEntry(
                    (string)record[nameof(AdoNetGrainDirectoryEntry.ClusterId)],
                    (string)record[nameof(AdoNetGrainDirectoryEntry.ProviderId)],
                    (string)record[nameof(AdoNetGrainDirectoryEntry.GrainId)],
                    (string)record[nameof(AdoNetGrainDirectoryEntry.SiloAddress)],
                    (string)record[nameof(AdoNetGrainDirectoryEntry.ActivationId)]),
                command => new DbStoredQueries.Columns(command)
                {
                    ClusterId = clusterId,
                    ProviderId = providerId,
                    GrainId = grainId,
                    SiloAddressAsString = siloAddress,
                    ActivationId = activationId
                },
                result => result.Single());
        }

        /// <summary>
        /// Unregisters a grain activation.
        /// </summary>
        /// <param name="clusterId">The cluster identifier.</param>
        /// <param name="providerId">The grain directory provider identifier.</param>
        /// <param name="grainId">The grain identifier.</param>
        /// <param name="activationId">The activation identifier.</param>
        /// <returns>The count of rows affected.</returns>
        internal Task<int> UnregisterGrainActivationAsync(string clusterId, string providerId, string grainId, string activationId)
        {
            ArgumentNullException.ThrowIfNull(clusterId);
            ArgumentNullException.ThrowIfNull(providerId);
            ArgumentNullException.ThrowIfNull(grainId);
            ArgumentNullException.ThrowIfNull(activationId);

            return ReadAsync(
                dbStoredQueries.UnregisterGrainActivationKey,
                record => record.GetInt32(0),
                command => new DbStoredQueries.Columns(command)
                {
                    ClusterId = clusterId,
                    ProviderId = providerId,
                    GrainId = grainId,
                    ActivationId = activationId
                },
                result => result.Single());
        }

        /// <summary>
        /// Looks up a grain activation.
        /// </summary>
        /// <param name="clusterId">The cluster identifier.</param>
        /// <param name="providerId">The grain directory provider identifier.</param>
        /// <param name="grainId">The grain identifier.</param>
        /// <returns>The grain activation if found or null if not.</returns>
        internal Task<AdoNetGrainDirectoryEntry?> LookupGrainActivationAsync(string clusterId, string providerId, string grainId)
        {
            ArgumentNullException.ThrowIfNull(clusterId);
            ArgumentNullException.ThrowIfNull(providerId);
            ArgumentNullException.ThrowIfNull(grainId);

            return ReadAsync(
                dbStoredQueries.LookupGrainActivationKey,
                record => new AdoNetGrainDirectoryEntry(
                    (string)record[nameof(AdoNetGrainDirectoryEntry.ClusterId)],
                    (string)record[nameof(AdoNetGrainDirectoryEntry.ProviderId)],
                    (string)record[nameof(AdoNetGrainDirectoryEntry.GrainId)],
                    (string)record[nameof(AdoNetGrainDirectoryEntry.SiloAddress)],
                    (string)record[nameof(AdoNetGrainDirectoryEntry.ActivationId)]),
                command => new DbStoredQueries.Columns(command)
                {
                    ClusterId = clusterId,
                    ProviderId = providerId,
                    GrainId = grainId,
                },
                result => result.SingleOrDefault());
        }

        /// <summary>
        /// Unregisters all grain activations for a set of silos.
        /// </summary>
        /// <param name="clusterId">The cluster identifier.</param>
        /// <param name="providerId">The grain directory provider identifier.</param>
        /// <param name="siloAddresses">The pipe separated set of silos.</param>
        /// <returns>The count of rows affected.</returns>
        internal Task<int> UnregisterGrainActivationsAsync(string clusterId, string providerId, string siloAddresses)
        {
            ArgumentNullException.ThrowIfNull(clusterId);
            ArgumentNullException.ThrowIfNull(providerId);
            ArgumentNullException.ThrowIfNull(siloAddresses);

            return ReadAsync(
                dbStoredQueries.UnregisterGrainActivationsKey,
                record => record.GetInt32(0),
                command => new DbStoredQueries.Columns(command)
                {
                    ClusterId = clusterId,
                    ProviderId = providerId,
                    SiloAddresses = siloAddresses
                },
                result => result.Single());
        }

#endif
    }
}

#nullable restore
