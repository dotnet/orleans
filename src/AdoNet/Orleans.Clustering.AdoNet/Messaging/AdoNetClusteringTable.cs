using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Clustering.AdoNet.Storage;
using Orleans.Configuration;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Implements Orleans cluster membership using a relational database.
    /// </summary>
    public partial class AdoNetClusteringTable : IMembershipTable
    {
        private readonly string clusterId;
        private readonly IServiceProvider serviceProvider;
        private readonly ILogger logger;
        private RelationalOrleansQueries orleansQueries = null!;
        private readonly AdoNetClusteringSiloOptions clusteringTableOptions;

        /// <summary>
        /// Initializes a new instance of the <see cref="AdoNetClusteringTable"/> class.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="clusterOptions">The cluster identity options.</param>
        /// <param name="clusteringOptions">The relational clustering options.</param>
        /// <param name="logger">The logger.</param>
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

        /// <inheritdoc />
        [Obsolete("Use InitializeMembershipTableAsync instead.")]
        public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTableAsync(tryInitTableVersion, CancellationToken.None);

        /// <inheritdoc />
        public async Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogTraceInitializeMembershipTable();

            //This initializes all of Orleans operational queries from the database using a well known view
            //and assumes the database with appropriate definitions exists already.
            orleansQueries = await RelationalOrleansQueries.CreateInstance(
                clusteringTableOptions.Invariant,
                clusteringTableOptions.ConnectionString,
                clusteringTableOptions.DataSource,
                cancellationToken);

            // even if I am not the one who created the table,
            // try to insert an initial table version if it is not already there,
            // so we always have a first table version row, before this silo starts working.
            if (tryInitTableVersion)
            {
                var wasCreated = await InitTableAsync(cancellationToken);
                if (wasCreated)
                {
                    LogInfoCreatedNewTableVersionRow();
                }
            }
        }

        /// <inheritdoc />
        [Obsolete("Use ReadRowAsync instead.")]
        public Task<MembershipTableData> ReadRow(SiloAddress key) => ReadRowAsync(key, CancellationToken.None);

        /// <inheritdoc />
        public async Task<MembershipTableData> ReadRowAsync(SiloAddress key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogTraceReadRow(key);
            try
            {
                return await orleansQueries.MembershipReadRowAsync(this.clusterId, key, cancellationToken);
            }
            catch (Exception ex)
            {
                LogDebugReadRowFailed(ex);
                throw;
            }
        }

        /// <inheritdoc />
        [Obsolete("Use ReadAllAsync instead.")]
        public Task<MembershipTableData> ReadAll() => ReadAllAsync(CancellationToken.None);

        /// <inheritdoc />
        public async Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogTraceReadAll();
            try
            {
                return await orleansQueries.MembershipReadAllAsync(this.clusterId, cancellationToken);
            }
            catch (Exception ex)
            {
                LogDebugReadAllFailed(ex);
                throw;
            }
        }

        /// <inheritdoc />
        [Obsolete("Use InsertRowAsync instead.")]
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRowAsync(entry, tableVersion, CancellationToken.None);

        /// <inheritdoc />
        public async Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                return await orleansQueries.InsertMembershipRowAsync(this.clusterId, entry, tableVersion.VersionEtag, cancellationToken);
            }
            catch (Exception ex)
            {
                LogDebugInsertRowFailed(ex);
                throw;
            }
        }

        /// <inheritdoc />
        [Obsolete("Use UpdateRowAsync instead.")]
        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRowAsync(entry, etag, tableVersion, CancellationToken.None);

        /// <inheritdoc />
        public async Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                return await orleansQueries.UpdateMembershipRowAsync(this.clusterId, entry, tableVersion.VersionEtag, cancellationToken);
            }
            catch (Exception ex)
            {
                LogDebugUpdateRowFailed(ex);
                throw;
            }
        }

        /// <inheritdoc />
        [Obsolete("Use UpdateIAmAliveAsync instead.")]
        public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);

        /// <inheritdoc />
        public async Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogTraceUpdateIAmAlive(entry);
            if (entry == null)
            {
                LogDebugUpdateIAmAliveAbortedNullEntry();
                throw new ArgumentNullException(nameof(entry));
            }
            try
            {
                await orleansQueries.UpdateIAmAliveTimeAsync(this.clusterId, entry.SiloAddress, entry.IAmAliveTime, cancellationToken);
            }
            catch (Exception ex)
            {
                LogDebugUpdateIAmAliveFailed(ex);
                throw;
            }
        }

        /// <inheritdoc />
        [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
        public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

        /// <inheritdoc />
        public async Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogTraceDeleteMembershipTableEntries(clusterId);
            try
            {
                await orleansQueries.DeleteMembershipTableEntriesAsync(clusterId, cancellationToken);
            }
            catch (Exception ex)
            {
                LogDebugDeleteMembershipTableEntriesFailed(ex);
                throw;
            }
        }

        /// <inheritdoc />
        [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

        /// <inheritdoc />
        public async Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LogTraceCleanupDefunctSiloEntries(beforeDate, clusterId);
            try
            {
                await orleansQueries.CleanupDefunctSiloEntriesAsync(beforeDate, this.clusterId, cancellationToken);
            }
            catch (Exception ex)
            {
                LogDebugCleanupDefunctSiloEntriesFailed(ex);
                throw;
            }
        }

        private async Task<bool> InitTableAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await orleansQueries.InsertMembershipVersionRowAsync(this.clusterId, cancellationToken);
            }
            catch (Exception ex)
            {
                LogTraceInsertSiloMembershipVersionFailed(ex);
                throw;
            }
        }

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(InitializeMembershipTableAsync)} called."
        )]
        private partial void LogTraceInitializeMembershipTable();

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Created new table version row."
        )]
        private partial void LogInfoCreatedNewTableVersionRow();

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(ReadRowAsync)} called with key: {{Key}}."
        )]
        private partial void LogTraceReadRow(SiloAddress key);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(ReadRowAsync)} failed"
        )]
        private partial void LogDebugReadRowFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(ReadAllAsync)} called."
        )]
        private partial void LogTraceReadAll();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(ReadAllAsync)} failed"
        )]
        private partial void LogDebugReadAllFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(InsertRowAsync)} called with entry {{Entry}} and tableVersion {{TableVersion}}."
        )]
        private partial void LogTraceInsertRow(MembershipEntry entry, TableVersion tableVersion);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(InsertRowAsync)} aborted due to null check. MembershipEntry is null."
        )]
        private partial void LogDebugInsertRowAbortedNullEntry();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(InsertRowAsync)} aborted due to null check. TableVersion is null "
        )]
        private partial void LogDebugInsertRowAbortedNullTableVersion();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(InsertRowAsync)} failed"
        )]
        private partial void LogDebugInsertRowFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(IMembershipTable)}.{nameof(UpdateRowAsync)} called with entry {{Entry}}, etag {{ETag}} and tableVersion {{TableVersion}}."
        )]
        private partial void LogTraceUpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(UpdateRowAsync)} aborted due to null check. MembershipEntry is null."
        )]
        private partial void LogDebugUpdateRowAbortedNullEntry();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(UpdateRowAsync)} aborted due to null check. TableVersion is null"
        )]
        private partial void LogDebugUpdateRowAbortedNullTableVersion();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(UpdateRowAsync)} failed"
        )]
        private partial void LogDebugUpdateRowFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(IMembershipTable)}.{nameof(UpdateIAmAliveAsync)} called with entry {{Entry}}."
        )]
        private partial void LogTraceUpdateIAmAlive(MembershipEntry entry);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(UpdateIAmAliveAsync)} aborted due to null check. MembershipEntry is null."
        )]
        private partial void LogDebugUpdateIAmAliveAbortedNullEntry();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(UpdateIAmAliveAsync)} failed"
        )]
        private partial void LogDebugUpdateIAmAliveFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(IMembershipTable)}.{nameof(DeleteMembershipTableEntriesAsync)} called with clusterId {{ClusterId}}."
        )]
        private partial void LogTraceDeleteMembershipTableEntries(string clusterId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(DeleteMembershipTableEntriesAsync)} failed"
        )]
        private partial void LogDebugDeleteMembershipTableEntriesFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = $"{nameof(IMembershipTable)}.{nameof(CleanupDefunctSiloEntriesAsync)} called with beforeDate {{beforeDate}} and clusterId {{ClusterId}}."
        )]
        private partial void LogTraceCleanupDefunctSiloEntries(DateTimeOffset beforeDate, string clusterId);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = $"{nameof(AdoNetClusteringTable)}.{nameof(CleanupDefunctSiloEntriesAsync)} failed"
        )]
        private partial void LogDebugCleanupDefunctSiloEntriesFailed(Exception exception);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Insert silo membership version failed"
        )]
        private partial void LogTraceInsertSiloMembershipVersionFailed(Exception exception);
    }
}
