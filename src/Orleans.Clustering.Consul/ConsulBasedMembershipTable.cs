using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
        private readonly ILogger _logger;
        private readonly IConsulClient _consulClient;
        private readonly ConsulClusteringOptions clusteringSiloTableOptions;
        private readonly string clusterId;
        private readonly string kvRootFolder;
        private readonly string versionKey;

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
        /// <param name="tryInitTableVersion">Will be ignored: Consul does not support the extended Membership Protocol TableVersion</param>
        /// <returns></returns>
        /// <remarks>
        /// Consul Membership Provider does not support the extended Membership Protocol,
        /// therefore there is no MembershipTable to Initialize
        /// </remarks>
        public Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            return Task.CompletedTask;
        }


        public async Task<MembershipTableData> ReadRow(SiloAddress siloAddress)
        {
            var (siloRegistration, tableVersion) = await GetConsulSiloRegistration(siloAddress);

            return AssembleMembershipTableData(tableVersion, siloRegistration);
        }

        public Task<MembershipTableData> ReadAll()
        {
            return ReadAll(this._consulClient, this.clusterId, this.kvRootFolder, this._logger, this.versionKey);
        }

        public static async Task<MembershipTableData> ReadAll(IConsulClient consulClient, string clusterId, string kvRootFolder, ILogger logger, string versionKey)
        {
            var deploymentKVAddresses = await consulClient.KV.List(ConsulSiloRegistrationAssembler.FormatDeploymentKVPrefix(clusterId, kvRootFolder));
            if (deploymentKVAddresses.Response == null)
            {
                LogDebugCouldNotFindSiloRegistrations(logger, clusterId);
                return new MembershipTableData(NotFoundTableVersion);
            }

            var allSiloRegistrations =
                deploymentKVAddresses.Response
                .Where(siloKV => !siloKV.Key.EndsWith(ConsulSiloRegistrationAssembler.SiloIAmAliveSuffix, StringComparison.OrdinalIgnoreCase)
                        && !siloKV.Key.EndsWith(ConsulSiloRegistrationAssembler.VersionSuffix, StringComparison.OrdinalIgnoreCase))
                .Select(siloKV =>
                {
                    var iAmAliveKV = deploymentKVAddresses.Response.SingleOrDefault(kv => kv.Key.Equals(ConsulSiloRegistrationAssembler.FormatSiloIAmAliveKey(siloKV.Key), StringComparison.OrdinalIgnoreCase));
                    return ConsulSiloRegistrationAssembler.FromKVPairs(clusterId, siloKV, iAmAliveKV);
                }).ToArray();

            var tableVersion = GetTableVersion(versionKey, deploymentKVAddresses);

            return AssembleMembershipTableData(tableVersion, allSiloRegistrations);
        }

        public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            try
            {
                //Use "0" as the eTag then Consul KV CAS will treat the operation as an insert and return false if the KV already exiats.
                var siloRegistration = ConsulSiloRegistrationAssembler.FromMembershipEntry(this.clusterId, entry, "0");
                var insertKV = ConsulSiloRegistrationAssembler.ToKVPair(siloRegistration, this.kvRootFolder);
                var rowInsert = new KVTxnOp(insertKV.Key, KVTxnVerb.CAS) { Index = siloRegistration.LastIndex, Value = insertKV.Value };
                var versionUpdate = this.GetVersionRowUpdate(tableVersion);

                var responses = await _consulClient.KV.Txn(new List<KVTxnOp> { rowInsert, versionUpdate });
                if (!responses.Response.Success)
                {
                    LogDebugConsulMembershipProviderFailedToInsertRow(entry.SiloAddress);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogInformationConsulMembershipProviderFailedToInsertRegistration(ex, entry.SiloAddress);
                throw;
            }
        }

        public async Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            //Update Silo Liveness
            try
            {
                var siloRegistration = ConsulSiloRegistrationAssembler.FromMembershipEntry(this.clusterId, entry, etag);
                var updateKV = ConsulSiloRegistrationAssembler.ToKVPair(siloRegistration, this.kvRootFolder);

                var rowUpdate = new KVTxnOp(updateKV.Key, KVTxnVerb.CAS) { Index = siloRegistration.LastIndex, Value = updateKV.Value };
                var versionUpdate = this.GetVersionRowUpdate(tableVersion);

                var responses = await _consulClient.KV.Txn(new List<KVTxnOp> { rowUpdate, versionUpdate });
                if (!responses.Response.Success)
                {
                    LogDebugConsulMembershipProviderFailedCASCheck(entry.SiloAddress);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                LogInformationConsulMembershipProviderFailedToUpdateRegistration(ex, entry.SiloAddress);
                throw;
            }
        }

        public async Task UpdateIAmAlive(MembershipEntry entry)
        {
            var iAmAliveKV = ConsulSiloRegistrationAssembler.ToIAmAliveKVPair(this.clusterId, this.kvRootFolder, entry.SiloAddress, entry.IAmAliveTime);
            await _consulClient.KV.Put(iAmAliveKV);
        }

        public async Task DeleteMembershipTableEntries(string clusterId)
        {
            await _consulClient.KV.DeleteTree(ConsulSiloRegistrationAssembler.FormatDeploymentKVPrefix(this.clusterId, this.kvRootFolder));
        }

        private static TableVersion GetTableVersion(string versionKey, QueryResult<KVPair[]> entries)
        {
            TableVersion tableVersion;
            var tableVersionEntry = entries?.Response?.FirstOrDefault(kv => kv.Key.Equals(versionKey ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            if (tableVersionEntry != null)
            {
                var versionNumber = 0;
                if (tableVersionEntry.Value is byte[] versionData && versionData.Length > 0)
                {
                    int.TryParse(Encoding.UTF8.GetString(tableVersionEntry.Value), out versionNumber);
                }

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
            ulong.TryParse(version.VersionEtag, out var index);
            var versionBytes = Encoding.UTF8.GetBytes(version.Version.ToString(CultureInfo.InvariantCulture));
            return new KVTxnOp(this.versionKey, KVTxnVerb.CAS) { Index = index, Value = versionBytes };
        }

        private async Task<(ConsulSiloRegistration, TableVersion)> GetConsulSiloRegistration(SiloAddress siloAddress)
        {
            var deploymentKey = ConsulSiloRegistrationAssembler.FormatDeploymentKVPrefix(this.clusterId, this.kvRootFolder);
            var siloKey = ConsulSiloRegistrationAssembler.FormatDeploymentSiloKey(this.clusterId, this.kvRootFolder, siloAddress);
            var entries = await _consulClient.KV.List(deploymentKey);
            if (entries.Response == null) return (null, NotFoundTableVersion);

            var siloKV = entries.Response.Single(KV => KV.Key.Equals(siloKey, StringComparison.OrdinalIgnoreCase));
            var iAmAliveKV = entries.Response.SingleOrDefault(KV => KV.Key.Equals(ConsulSiloRegistrationAssembler.FormatSiloIAmAliveKey(siloKey), StringComparison.OrdinalIgnoreCase));
            var tableVersion = GetTableVersion(versionKey: versionKey, entries: entries);

            var siloRegistration = ConsulSiloRegistrationAssembler.FromKVPairs(this.clusterId, siloKV, iAmAliveKV);

            return (siloRegistration, tableVersion);
        }

        private static MembershipTableData AssembleMembershipTableData(TableVersion tableVersion, params ConsulSiloRegistration[] silos)
        {
            var membershipEntries = silos
                .Where(silo => silo != null)
                .Select(silo => ConsulSiloRegistrationAssembler.ToMembershipEntry(silo))
                .ToList();

            return new MembershipTableData(membershipEntries, tableVersion);
        }

        public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            var allKVs = await _consulClient.KV.List(ConsulSiloRegistrationAssembler.FormatDeploymentKVPrefix(this.clusterId, this.kvRootFolder));
            if (allKVs.Response == null)
            {
                LogDebugCouldNotFindSiloRegistrationsForCleanup(this.clusterId);
                return;
            }

            var allRegistrations =
                allKVs.Response
                .Where(siloKV => !siloKV.Key.EndsWith(ConsulSiloRegistrationAssembler.SiloIAmAliveSuffix, StringComparison.OrdinalIgnoreCase)
                    && !siloKV.Key.EndsWith(ConsulSiloRegistrationAssembler.VersionSuffix, StringComparison.OrdinalIgnoreCase))
                .Select(siloKV =>
                {
                    var iAmAliveKV = allKVs.Response.SingleOrDefault(kv => kv.Key.Equals(ConsulSiloRegistrationAssembler.FormatSiloIAmAliveKey(siloKV.Key), StringComparison.OrdinalIgnoreCase));
                    return new
                    {
                        RegistrationKey = siloKV.Key,
                        Registration = ConsulSiloRegistrationAssembler.FromKVPairs(clusterId, siloKV, iAmAliveKV)
                    };
                }).ToArray();

            foreach (var entry in allRegistrations)
            {
                if (entry.Registration.IAmAliveTime < beforeDate && entry.Registration.Status != SiloStatus.Active)
                {
                    await _consulClient.KV.DeleteTree(entry.RegistrationKey);
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
