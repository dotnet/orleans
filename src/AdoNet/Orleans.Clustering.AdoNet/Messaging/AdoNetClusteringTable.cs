using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Clustering.AdoNet.Storage;
using Orleans.Configuration;

namespace Orleans.Runtime.MembershipService
{
    public partial class AdoNetClusteringTable : IMembershipTable
    {
        private readonly string clusterId;
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger logger;
        private RelationalOrleansQueries orleansQueries;
        private readonly AdoNetClusteringSiloOptions clusteringTableOptions;

        public AdoNetClusteringTable(
            IServiceProvider serviceProvider,
            IOptions<ClusterOptions> clusterOptions,
            IOptions<AdoNetClusteringSiloOptions> clusteringOptions,
            ILogger<AdoNetClusteringTable> logger)
        {
            this.serviceProvider = serviceProvider;
            this.logger = logger;
            this.clusteringTableOptions = clusteringOptions.Value;
            this.clusterId = clusterOptions.Value.ClusterId;
        }

        public async Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            LogTraceInitializeMembershipTable();

            //This initializes all of Orleans operational queries from the database using a well known view
            //and assumes the database with appropriate definitions exists already.
            orleansQueries = await RelationalOrleansQueries.CreateInstance(
                clusteringTableOptions.Invariant,
                clusteringTableOptions.ConnectionString);

            // even if I am not the one who created the table,
            // try to insert an initial table version if it is not already there,
            // so we always have a first table version row, before this silo starts working.
            if (tryInitTableVersion)
            {
                var wasCreated = await InitTableAsync();
                if (wasCreated)
                {
                    LogInfoCreatedNewTableVersionRow();
                }
            }
        }

        public async Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            LogTraceReadRow(key);
            try
            {
                return await orleansQueries.MembershipReadRowAsync(this.clusterId, key);
            }
            catch (Exception ex)
            {
                LogDebugReadRowFailed(ex);
                throw;
            }
        }

        public async Task<MembershipTableData> ReadAll()
        {
            LogTraceReadAll();
            try
            {
                return await orleansQueries.MembershipReadAllAsync(this.clusterId);
            }
            catch (Exception ex)
            {
                LogDebugReadAllFailed(ex);
                throw;
            }
        }

        public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            LogTraceInsertRow(entry, tableVersion);

            //The "tableVersion" parameter should always exist when inserting a row as Init should
            //have been called and membership version created and read. This is an optimization to
            //not to go through all the way to database to fail a conditional check on etag (which does
            //exist for the sake of robustness) as mandated by Orleans membership protocol.
            //Likewise, no update can be done without membership entry.
            if (entry == null)
            {
                LogDebugInsertRowAbortedNullEntry();
                throw new ArgumentNullException(nameof(entry));
            }
            if (tableVersion is null)
            {
                LogDebugInsertRowAbortedNullTableVersion();
                throw new ArgumentNullException(nameof(tableVersion));
            }

            try
            {
                return await orleansQueries.InsertMembershipRowAsync(this.clusterId, entry, tableVersion.VersionEtag);
            }
            catch (Exception ex)
            {
                LogDebugInsertRowFailed(ex);
                throw;
            }
        }

        public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            LogTraceUpdateRow(entry, etag, tableVersion);

            //The "tableVersion" parameter should always exist when updating a row as Init should
            //have been called and membership version created and read. This is an optimization to
            //not to go through all the way to database to fail a conditional check (which does
            //exist for the sake of robustness) as mandated by Orleans membership protocol.
            //Likewise, no update can be done without membership entry or an etag.
            if (entry == null)
            {
                LogDebugUpdateRowAbortedNullEntry();
                throw new ArgumentNullException(nameof(entry));
            }
            if (tableVersion is null)
            {
                LogDebugUpdateRowAbortedNullTableVersion();
                throw new ArgumentNullException(nameof(tableVersion));
            }

            try
            {
                return await orleansQueries.UpdateMembershipRowAsync(this.clusterId, entry, tableVersion.VersionEtag);
            }
            catch (Exception ex)
            {
                LogDebugUpdateRowFailed(ex);
                throw;
            }
        }

        public async Task UpdateIAmAlive(MembershipEntry entry)
        {
            LogTraceUpdateIAmAlive(entry);
            if (entry == null)
            {
                LogDebugUpdateIAmAliveAbortedNullEntry();
                throw new ArgumentNullException(nameof(entry));
            }
            try
            {
                await orleansQueries.UpdateIAmAliveTimeAsync(this.clusterId, entry.SiloAddress, entry.IAmAliveTime);
            }
            catch (Exception ex)
            {
                LogDebugUpdateIAmAliveFailed(ex);
                throw;
            }
        }

        public async Task DeleteMembershipTableEntries(string clusterId)
        {
            LogTraceDeleteMembershipTableEntries(clusterId);
            try
            {
                await orleansQueries.DeleteMembershipTableEntriesAsync(clusterId);
            }
            catch (Exception ex)
            {
                LogDebugDeleteMembershipTableEntriesFailed(ex);
                throw;
            }
        }

        public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            LogTraceCleanupDefunctSiloEntries(beforeDate, clusterId);
            try
            {
                await orleansQueries.CleanupDefunctSiloEntriesAsync(beforeDate, this.clusterId);
            }
            catch (Exception ex)
            {
                LogDebugCleanupDefunctSiloEntriesFailed(ex);
                throw;
            }
        }

        private async Task<bool> InitTableAsync()
        {
            try
            {
                return await orleansQueries.InsertMembershipVersionRowAsync(this.clusterId);
            }
            catch (Exception ex)
            {
                LogTraceInsertSiloMembershipVersionFailed(ex);
                throw;
            }
        }

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(InitializeMembershipTable)} called."
        )]
        private partial void LogTraceInitializeMembershipTable();

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Created new table version row."
        )]
        private partial void LogInfoCreatedNewTableVersionRow();

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(ReadRow)} called with key: {{Key}}."
        )]
        private partial void LogTraceReadRow(SiloAddress key);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(ReadRow)} failed"
        )]
        private partial void LogDebugReadRowFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(ReadAll)} called."
        )]
        private partial void LogTraceReadAll();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(ReadAll)} failed"
        )]
        private partial void LogDebugReadAllFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(InsertRow)} called with entry {{Entry}} and tableVersion {{TableVersion}}."
        )]
        private partial void LogTraceInsertRow(MembershipEntry entry, TableVersion tableVersion);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(InsertRow)} aborted due to null check. MembershipEntry is null."
        )]
        private partial void LogDebugInsertRowAbortedNullEntry();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(InsertRow)} aborted due to null check. TableVersion is null "
        )]
        private partial void LogDebugInsertRowAbortedNullTableVersion();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(InsertRow)} failed"
        )]
        private partial void LogDebugInsertRowFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(IMembershipTable)}.{nameof(UpdateRow)} called with entry {{Entry}}, etag {{ETag}} and tableVersion {{TableVersion}}."
        )]
        private partial void LogTraceUpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(UpdateRow)} aborted due to null check. MembershipEntry is null."
        )]
        private partial void LogDebugUpdateRowAbortedNullEntry();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(UpdateRow)} aborted due to null check. TableVersion is null"
        )]
        private partial void LogDebugUpdateRowAbortedNullTableVersion();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(UpdateRow)} failed"
        )]
        private partial void LogDebugUpdateRowFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(IMembershipTable)}.{nameof(UpdateIAmAlive)} called with entry {{Entry}}."
        )]
        private partial void LogTraceUpdateIAmAlive(MembershipEntry entry);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(UpdateIAmAlive)} aborted due to null check. MembershipEntry is null."
        )]
        private partial void LogDebugUpdateIAmAliveAbortedNullEntry();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(UpdateIAmAlive)} failed"
        )]
        private partial void LogDebugUpdateIAmAliveFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(IMembershipTable)}.{nameof(DeleteMembershipTableEntries)} called with clusterId {{ClusterId}}."
        )]
        private partial void LogTraceDeleteMembershipTableEntries(string clusterId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(DeleteMembershipTableEntries)} failed"
        )]
        private partial void LogDebugDeleteMembershipTableEntriesFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(IMembershipTable)}.{nameof(CleanupDefunctSiloEntries)} called with beforeDate {{beforeDate}} and clusterId {{ClusterId}}."
        )]
        private partial void LogTraceCleanupDefunctSiloEntries(DateTimeOffset beforeDate, string clusterId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(CleanupDefunctSiloEntries)} failed"
        )]
        private partial void LogDebugCleanupDefunctSiloEntriesFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Insert silo membership version failed"
        )]
        private partial void LogTraceInsertSiloMembershipVersionFailed(Exception exception);
    }
}
