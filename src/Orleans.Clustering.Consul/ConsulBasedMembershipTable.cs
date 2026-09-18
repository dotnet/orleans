using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Consul;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Host;

namespace Orleans.Runtime.Membership
{
    /// <summary>
    /// A Membership Table implementation using Consul 0.6.0  https://consul.io/
    /// </summary>
    public partial class ConsulBasedMembershipTable : IMembershipTable
    {
        private static readonly TableVersion NotFoundTableVersion = new TableVersion(0, "0");
        private static readonly QueryOptions ConsistentRead = new() { Consistency = ConsistencyMode.Consistent };
        private static readonly TimeSpan ConflictRetryDelay = TimeSpan.FromMilliseconds(100);
        private readonly ILogger _logger;
        private readonly IConsulClient _consulClient;
        private readonly ConsulClusteringOptions clusteringSiloTableOptions;
        private readonly string clusterId;
        private readonly string? kvRootFolder;
        private readonly string versionKey;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConsulBasedMembershipTable"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="membershipTableOptions">The Consul clustering options.</param>
        /// <param name="clusterOptions">The cluster identity options.</param>
        public ConsulBasedMembershipTable(
            ILogger<ConsulBasedMembershipTable> logger,
            IOptions<ConsulClusteringOptions> membershipTableOptions,
            IOptions<ClusterOptions> clusterOptions)
        {
            this.clusterId = clusterOptions.Value.ClusterId;
            this.kvRootFolder = membershipTableOptions.Value.KvRootFolder;
            this._logger = logger;
            this.clusteringSiloTableOptions = membershipTableOptions.Value;
            this._consulClient = this.clusteringSiloTableOptions.CreateClient();
            versionKey = ConsulSiloRegistrationAssembler.FormatVersionKey(clusterId, kvRootFolder);
        }

        /// <summary>
        /// Initializes the Consul based membership table.
        /// </summary>
        /// <param name="tryInitTableVersion">Whether to create the initial table version if it does not exist.</param>
        /// <returns></returns>
        [Obsolete("Use InitializeMembershipTableAsync instead.")]
        public Task InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTableAsync(tryInitTableVersion, CancellationToken.None);

        /// <inheritdoc />
        public async Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tryInitTableVersion)
            {
                await _consulClient.KV.Txn(
                    new List<KVTxnOp> { GetVersionRowUpdate(NotFoundTableVersion) }, cancellationToken);
            }
        }

        /// <inheritdoc />
        [Obsolete("Use ReadRowAsync instead.")]
        public Task<MembershipTableData> ReadRow(SiloAddress siloAddress) => ReadRowAsync(siloAddress, CancellationToken.None);

        /// <inheritdoc />
        public async Task<MembershipTableData> ReadRowAsync(SiloAddress siloAddress, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (siloRegistration, tableVersion, _) = await GetConsulSiloRegistration(siloAddress, cancellationToken);

            return AssembleMembershipTableData(tableVersion, siloRegistration);
        }

        /// <inheritdoc />
        [Obsolete("Use ReadAllAsync instead.")]
        public Task<MembershipTableData> ReadAll() => ReadAllAsync(CancellationToken.None);

        /// <inheritdoc />
        public Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            return ReadAllAsync(this._consulClient, this.clusterId, this.kvRootFolder, this._logger, this.versionKey, cancellationToken);
        }

        /// <summary>
        /// Reads all membership entries for a cluster from Consul.
        /// </summary>
        /// <param name="consulClient">The Consul client.</param>
        /// <param name="clusterId">The cluster identifier.</param>
        /// <param name="kvRootFolder">The optional root folder containing Orleans keys.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="versionKey">The key containing the membership table version, or <see langword="null"/> to use the cluster's default version key.</param>
        /// <returns>The cluster membership entries and table version.</returns>
        [Obsolete("Use ReadAllAsync instead.")]
        public static Task<MembershipTableData> ReadAll(IConsulClient consulClient, string clusterId, string? kvRootFolder, ILogger logger, string? versionKey) =>
            ReadAllAsync(consulClient, clusterId, kvRootFolder, logger, versionKey, CancellationToken.None);

        /// <summary>
        /// Reads all membership entries for a cluster from Consul.
        /// </summary>
        /// <param name="consulClient">The Consul client.</param>
        /// <param name="clusterId">The cluster identifier.</param>
        /// <param name="kvRootFolder">The optional root folder containing Orleans keys.</param>
        /// <param name="logger">The logger.</param>
        /// <param name="versionKey">The key containing the membership table version, or <see langword="null"/> to use the cluster's default version key.</param>
        /// <param name="cancellationToken">A token which cancels the operation.</param>
        /// <returns>The cluster membership entries and table version.</returns>
        public static async Task<MembershipTableData> ReadAllAsync(IConsulClient consulClient, string clusterId, string? kvRootFolder, ILogger logger, string? versionKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var deploymentKVAddresses = await consulClient.KV.List(ConsulSiloRegistrationAssembler.FormatDeploymentKVPrefix(clusterId, kvRootFolder) + "/", ConsistentRead, cancellationToken);
            if (deploymentKVAddresses.Response == null)
            {
                LogDebugCouldNotFindSiloRegistrations(logger, clusterId);
                return new MembershipTableData(NotFoundTableVersion);
            }

            var allSiloRegistrations =
                deploymentKVAddresses.Response
                .Where(siloKV => !siloKV.Key.EndsWith("/" + ConsulSiloRegistrationAssembler.SiloIAmAliveSuffix, StringComparison.Ordinal)
                        && !siloKV.Key.EndsWith("/" + ConsulSiloRegistrationAssembler.VersionSuffix, StringComparison.Ordinal))
                .Select(siloKV =>
                {
                    var iAmAliveKV = deploymentKVAddresses.Response.SingleOrDefault(kv => kv.Key.Equals(ConsulSiloRegistrationAssembler.FormatSiloIAmAliveKey(siloKV.Key), StringComparison.Ordinal));
                    return ConsulSiloRegistrationAssembler.FromKVPairs(clusterId, siloKV, iAmAliveKV);
                }).ToArray();

            var tableVersionKey = versionKey ?? ConsulSiloRegistrationAssembler.FormatVersionKey(clusterId, kvRootFolder);
            var tableVersion = GetTableVersion(deploymentKVAddresses.Response.SingleOrDefault(kv => kv.Key.Equals(tableVersionKey, StringComparison.Ordinal)));

            return AssembleMembershipTableData(tableVersion, allSiloRegistrations);
        }

        /// <inheritdoc />
        [Obsolete("Use InsertRowAsync instead.")]
        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRowAsync(entry, tableVersion, CancellationToken.None);

        /// <inheritdoc />
        public async Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Consul treats a CAS index of zero as an insert.
                var siloRegistration = ConsulSiloRegistrationAssembler.FromMembershipEntry(this.clusterId, entry, "0");
                var insertKV = ConsulSiloRegistrationAssembler.ToKVPair(siloRegistration, this.kvRootFolder);
                var rowInsert = new KVTxnOp(insertKV.Key, KVTxnVerb.CAS) { Index = siloRegistration.LastIndex, Value = insertKV.Value };
                var versionUpdate = this.GetVersionRowUpdate(tableVersion);
                var heartbeat = ConsulSiloRegistrationAssembler.ToIAmAliveKVPair(this.clusterId, this.kvRootFolder, entry.SiloAddress, entry.IAmAliveTime);
                var heartbeatInsert = new KVTxnOp(heartbeat.Key, KVTxnVerb.CAS) { Index = 0, Value = heartbeat.Value };

                var responses = await _consulClient.KV.Txn(new List<KVTxnOp> { rowInsert, versionUpdate, heartbeatInsert }, cancellationToken);
                if (!responses.Response.Success)
                {
                    LogDebugConsulMembershipProviderFailedToInsertRow(entry.SiloAddress);
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogInformationConsulMembershipProviderFailedToInsertRegistration(ex, entry.SiloAddress);
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
            try
            {
                var expectedIndex = ulong.Parse(etag, CultureInfo.InvariantCulture);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (existing, currentVersion, currentHeartbeat) = await GetConsulSiloRegistration(entry.SiloAddress, cancellationToken);
                    if (existing is null || existing.LastIndex != expectedIndex
                        || currentVersion.VersionEtag != tableVersion.VersionEtag)
                    {
                        LogDebugConsulMembershipProviderFailedCASCheck(entry.SiloAddress);
                        return false;
                    }

                    var siloRegistration = ConsulSiloRegistrationAssembler.FromMembershipEntry(this.clusterId, entry, etag);
                    var updateKV = ConsulSiloRegistrationAssembler.ToKVPair(siloRegistration, this.kvRootFolder);
                    var heartbeat = ConsulSiloRegistrationAssembler.ToIAmAliveKVPair(clusterId, kvRootFolder, entry.SiloAddress,
                        new DateTime(Math.Max(entry.IAmAliveTime.Ticks, existing.IAmAliveTime.Ticks), DateTimeKind.Utc));
                    var operations = new List<KVTxnOp>
                    {
                        new(updateKV.Key, KVTxnVerb.CAS) { Index = expectedIndex, Value = updateKV.Value },
                        GetVersionRowUpdate(tableVersion),
                        new(heartbeat.Key, KVTxnVerb.CAS) { Index = currentHeartbeat?.ModifyIndex ?? 0, Value = heartbeat.Value }
                    };

                    if ((await _consulClient.KV.Txn(operations, cancellationToken)).Response.Success)
                    {
                        return true;
                    }

                    await Task.Delay(ConflictRetryDelay, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogInformationConsulMembershipProviderFailedToUpdateRegistration(ex, entry.SiloAddress);
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
            var heartbeat = ConsulSiloRegistrationAssembler.ToIAmAliveKVPair(clusterId, kvRootFolder, entry.SiloAddress, entry.IAmAliveTime);
            var response = await _consulClient.KV.Put(heartbeat, cancellationToken);
            if (!response.Response)
            {
                throw new OrleansException($"Consul failed to update the heartbeat for silo '{entry.SiloAddress}'.");
            }
        }

        /// <inheritdoc />
        [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
        public Task DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

        /// <inheritdoc />
        public async Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await _consulClient.KV.DeleteTree(ConsulSiloRegistrationAssembler.FormatDeploymentKVPrefix(clusterId, this.kvRootFolder) + "/", cancellationToken);
            if (!response.Response)
            {
                throw new OrleansException($"Consul failed to delete membership entries for cluster '{clusterId}'.");
            }
        }

        private static TableVersion GetTableVersion(KVPair? tableVersionEntry)
        {
            TableVersion tableVersion;
            if (tableVersionEntry != null)
            {
                var versionNumber = int.Parse(Encoding.UTF8.GetString(tableVersionEntry.Value), CultureInfo.InvariantCulture);
                tableVersion = new TableVersion(versionNumber, tableVersionEntry.ModifyIndex.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                tableVersion = NotFoundTableVersion;
            }

            return tableVersion;
        }

        private KVTxnOp GetVersionRowUpdate(TableVersion version)
        {
            var index = ulong.Parse(version.VersionEtag, CultureInfo.InvariantCulture);
            var versionBytes = Encoding.UTF8.GetBytes(version.Version.ToString(CultureInfo.InvariantCulture));
            return new KVTxnOp(this.versionKey, KVTxnVerb.CAS) { Index = index, Value = versionBytes };
        }

        private async Task<(ConsulSiloRegistration?, TableVersion, KVPair?)> GetConsulSiloRegistration(SiloAddress siloAddress, CancellationToken cancellationToken)
        {
            var siloKey = ConsulSiloRegistrationAssembler.FormatDeploymentSiloKey(this.clusterId, this.kvRootFolder, siloAddress);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var versionBefore = (await _consulClient.KV.Get(versionKey, ConsistentRead, cancellationToken)).Response;
                var entries = (await _consulClient.KV.List(siloKey, ConsistentRead, cancellationToken)).Response;
                var versionAfter = (await _consulClient.KV.Get(versionKey, ConsistentRead, cancellationToken)).Response;

                // An unchanged version ETag places the atomic row/heartbeat read within this table version.
                if (versionBefore?.ModifyIndex != versionAfter?.ModifyIndex)
                {
                    await Task.Delay(ConflictRetryDelay, cancellationToken);
                    continue;
                }

                var siloKV = entries?.SingleOrDefault(kv => kv.Key.Equals(siloKey, StringComparison.Ordinal));
                var iAmAliveKV = entries?.SingleOrDefault(kv => kv.Key.Equals(ConsulSiloRegistrationAssembler.FormatSiloIAmAliveKey(siloKey), StringComparison.Ordinal));
                var siloRegistration = siloKV is null ? null : ConsulSiloRegistrationAssembler.FromKVPairs(this.clusterId, siloKV, iAmAliveKV);

                return (siloRegistration, GetTableVersion(versionAfter), iAmAliveKV);
            }
        }

        private static MembershipTableData AssembleMembershipTableData(TableVersion tableVersion, params ConsulSiloRegistration?[] silos)
        {
            var membershipEntries = silos
                .OfType<ConsulSiloRegistration>()
                .Select(silo => ConsulSiloRegistrationAssembler.ToMembershipEntry(silo))
                .ToList();

            return new MembershipTableData(membershipEntries, tableVersion);
        }

        /// <inheritdoc />
        [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

        /// <inheritdoc />
        public async Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var allKVs = await _consulClient.KV.List(ConsulSiloRegistrationAssembler.FormatDeploymentKVPrefix(this.clusterId, this.kvRootFolder) + "/", ConsistentRead, cancellationToken);
            if (allKVs.Response == null)
            {
                LogDebugCouldNotFindSiloRegistrationsForCleanup(this.clusterId);
                return;
            }

            var allRegistrations =
                allKVs.Response
                .Where(siloKV => !siloKV.Key.EndsWith("/" + ConsulSiloRegistrationAssembler.SiloIAmAliveSuffix, StringComparison.Ordinal)
                    && !siloKV.Key.EndsWith("/" + ConsulSiloRegistrationAssembler.VersionSuffix, StringComparison.Ordinal))
                .Select(siloKV =>
                {
                    var iAmAliveKV = allKVs.Response.SingleOrDefault(kv => kv.Key.Equals(ConsulSiloRegistrationAssembler.FormatSiloIAmAliveKey(siloKV.Key), StringComparison.Ordinal));
                    return new
                    {
                        RegistrationKey = siloKV.Key,
                        Heartbeat = iAmAliveKV,
                        Registration = ConsulSiloRegistrationAssembler.FromKVPairs(clusterId, siloKV, iAmAliveKV)
                    };
                }).ToArray();

            foreach (var entry in allRegistrations)
            {
                if (entry.Registration.Status == SiloStatus.Dead
                    && Math.Max(entry.Registration.IAmAliveTime.Ticks, entry.Registration.StartTime.Ticks) < beforeDate.UtcDateTime.Ticks
                    && entry.Registration.SuspectingSilos?.Any(vote => vote.Time >= beforeDate.UtcDateTime) != true)
                {
                    var response = await _consulClient.KV.Txn(new List<KVTxnOp>
                    {
                        new(entry.RegistrationKey, KVTxnVerb.DeleteCAS) { Index = entry.Registration.LastIndex },
                        // DeleteCAS with index zero guards the absence of a legacy heartbeat key.
                        new(ConsulSiloRegistrationAssembler.FormatSiloIAmAliveKey(entry.RegistrationKey), KVTxnVerb.DeleteCAS) { Index = entry.Heartbeat?.ModifyIndex ?? 0 }
                    }, cancellationToken);
                    if (!response.Response.Success)
                    {
                        LogDebugConsulMembershipProviderFailedCASCheck(entry.Registration.Address);
                    }
                }
            }
        }

        [LoggerMessage(
            Level = Microsoft.Extensions.Logging.LogLevel.Debug,
            Message = "Could not find any silo registrations for deployment {ClusterId}."
        )]
        private static partial void LogDebugCouldNotFindSiloRegistrations(ILogger logger, string clusterId);

        [LoggerMessage(
            Level = Microsoft.Extensions.Logging.LogLevel.Debug,
            Message = "ConsulMembershipProvider failed to insert the row {SiloAddress}."
        )]
        private partial void LogDebugConsulMembershipProviderFailedToInsertRow(SiloAddress siloAddress);

        [LoggerMessage(
            Level = Microsoft.Extensions.Logging.LogLevel.Information,
            Message = "ConsulMembershipProvider failed to insert registration for silo {SiloAddress}"
        )]
        private partial void LogInformationConsulMembershipProviderFailedToInsertRegistration(Exception ex, SiloAddress siloAddress);

        [LoggerMessage(
            Level = Microsoft.Extensions.Logging.LogLevel.Debug,
            Message = "ConsulMembershipProvider failed the CAS check when updating the registration for silo {SiloAddress}."
        )]
        private partial void LogDebugConsulMembershipProviderFailedCASCheck(SiloAddress siloAddress);

        [LoggerMessage(
            Level = Microsoft.Extensions.Logging.LogLevel.Information,
            Message = "ConsulMembershipProvider failed to update the registration for silo {SiloAddress}"
        )]
        private partial void LogInformationConsulMembershipProviderFailedToUpdateRegistration(Exception ex, SiloAddress siloAddress);

        [LoggerMessage(
            Level = Microsoft.Extensions.Logging.LogLevel.Debug,
            Message = "Could not find any silo registrations for deployment {ClusterId}."
        )]
        private partial void LogDebugCouldNotFindSiloRegistrationsForCleanup(string clusterId);
    }
}
