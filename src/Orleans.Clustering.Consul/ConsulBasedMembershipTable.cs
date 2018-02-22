using System;
using System.Linq;
using System.Threading.Tasks;
using Consul;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Host;

namespace Orleans.Runtime.Membership
{
    /// <summary>
    /// A Membership Table implementation using Consul 0.6.0  https://consul.io/
    /// </summary>
    public class ConsulBasedMembershipTable : IMembershipTable
    {
        //Consul does not support the extended Membership Protocol and will always return the same table version information
        public static readonly TableVersion _tableVersion = new TableVersion(0, "0");

        private ILogger _logger;
        private readonly ConsulClient _consulClient;
        private readonly ConsulClusteringSiloOptions clusteringSiloTableOptions;
        private readonly string clusterId;

        public ConsulBasedMembershipTable(
            ILogger<ConsulBasedMembershipTable> logger,
            IOptions<ConsulClusteringSiloOptions> membershipTableOptions, 
            IOptions<ClusterOptions> clusterOptions)
        {
            this.clusterId = clusterOptions.Value.ClusterId;
            this._logger = logger;
            this.clusteringSiloTableOptions = membershipTableOptions.Value;
            _consulClient =
                new ConsulClient(config => config.Address = this.clusteringSiloTableOptions.Address);
        }

        /// <summary>
        /// Initializes the Consul based membership table.
        /// </summary>
        /// <param name="tryInitTableVersion">Will be ignored: Consul does not support the extended Membership Protocol TableVersion</param>
        /// <returns></returns>
        /// <remarks>
        /// Consul Membership Provider does not support the extended Membership Protocol,
        /// therefore there is no MembershipTable to Initialise
        /// </remarks>
        public Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            return Task.CompletedTask;
        }


        public async Task<MembershipTableData> ReadRow(SiloAddress siloAddress)
        {
            var siloRegistration = await GetConsulSiloRegistration(siloAddress);

            return AssembleMembershipTableData(siloRegistration);
        }

        public Task<MembershipTableData> ReadAll()
        {
            return ReadAll(this._consulClient, this.clusterId, this._logger);
        }

        public static async Task<MembershipTableData> ReadAll(ConsulClient consulClient, string clusterId, ILogger logger)
        {
            var deploymentKVAddresses = await consulClient.KV.List(ConsulSiloRegistrationAssembler.ParseDeploymentKVPrefix(clusterId));
            if (deploymentKVAddresses.Response == null)
            {
                logger.Debug("Could not find any silo registrations for deployment {0}.", clusterId);
                return new MembershipTableData(_tableVersion);
            }

            var allSiloRegistrations =
                deploymentKVAddresses.Response
                .Where(siloKV => !siloKV.Key.EndsWith(ConsulSiloRegistrationAssembler.SiloIAmAliveSuffix, StringComparison.OrdinalIgnoreCase))
                .Select(siloKV =>
                {
                    var iAmAliveKV = deploymentKVAddresses.Response.Where(kv => kv.Key.Equals(ConsulSiloRegistrationAssembler.ParseSiloIAmAliveKey(siloKV.Key), StringComparison.OrdinalIgnoreCase)).SingleOrDefault();
                    return ConsulSiloRegistrationAssembler.FromKVPairs(clusterId, siloKV, iAmAliveKV);
                }).ToArray();

            return AssembleMembershipTableData(allSiloRegistrations);
        }

        public async Task<Boolean> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            try
            {
                //Use "0" as the eTag then Consul KV CAS will treat the operation as an insert and return false if the KV already exiats.
                var consulSiloRegistration = ConsulSiloRegistrationAssembler.FromMembershipEntry(this.clusterId, entry, "0");
                var insertKV = ConsulSiloRegistrationAssembler.ToKVPair(consulSiloRegistration);

                var tryUpdate = await _consulClient.KV.CAS(insertKV);
                if (!tryUpdate.Response)
                {
                    _logger.Debug("ConsulMembershipProvider failed to insert the row because a registration already exists for silo {0}.", entry.SiloAddress);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.Info("ConsulMembershipProvider failed to insert registration for silo {0}; {1}.", entry.SiloAddress, ex);
                throw;
            }
        }

        public async Task<Boolean> UpdateRow(MembershipEntry entry, String etag, TableVersion tableVersion)
        {
            //Update Silo Liveness
            try
            {
                var siloRegistration = ConsulSiloRegistrationAssembler.FromMembershipEntry(this.clusterId, entry, etag);
                var updateKV = ConsulSiloRegistrationAssembler.ToKVPair(siloRegistration);

                //If the KV.CAS() call returns false then the update failed
                var tryUpdate = await _consulClient.KV.CAS(updateKV);
                if (!tryUpdate.Response)
                {
                    _logger.Debug("ConsulMembershipProvider failed the CAS check when updating the registration for silo {0}.", entry.SiloAddress);
                    return false;
                }

                return true;

            }
            catch (Exception ex)
            {
                _logger.Info("ConsulMembershipProvider failed to update the registration for silo {0}: {1}.", entry.SiloAddress, ex);
                throw;
            }
        }

        public async Task UpdateIAmAlive(MembershipEntry entry)
        {
            var iAmAliveKV = ConsulSiloRegistrationAssembler.ToIAmAliveKVPair(this.clusterId, entry.SiloAddress, entry.IAmAliveTime);
            await _consulClient.KV.Put(iAmAliveKV);
        }

        public async Task DeleteMembershipTableEntries(String clusterId)
        {
            await _consulClient.KV.DeleteTree(ConsulSiloRegistrationAssembler.ParseDeploymentKVPrefix(this.clusterId));
        }

        private async Task<ConsulSiloRegistration> GetConsulSiloRegistration(SiloAddress siloAddress)
        {
            var siloKey = ConsulSiloRegistrationAssembler.ParseDeploymentSiloKey(this.clusterId, siloAddress);
            var siloKVEntry = await _consulClient.KV.List(siloKey);
            if (siloKVEntry.Response == null) return null;

            var siloKV = siloKVEntry.Response.Single(KV => KV.Key.Equals(siloKey, StringComparison.OrdinalIgnoreCase));
            var iAmAliveKV = siloKVEntry.Response.SingleOrDefault(KV => KV.Key.Equals(ConsulSiloRegistrationAssembler.ParseSiloIAmAliveKey(siloKey), StringComparison.OrdinalIgnoreCase));

            var siloRegistration = ConsulSiloRegistrationAssembler.FromKVPairs(this.clusterId, siloKV, iAmAliveKV);

            return siloRegistration;
        }

        private static MembershipTableData AssembleMembershipTableData(params ConsulSiloRegistration[] silos)
        {
            var membershipEntries = silos
                .Where(silo => silo != null)
                .Select(silo => ConsulSiloRegistrationAssembler.ToMembershipEntry(silo))
                .ToList();

            return new MembershipTableData(membershipEntries, _tableVersion);
        }
    }
}
