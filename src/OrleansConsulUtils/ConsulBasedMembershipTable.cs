using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Consul;
using Orleans.Messaging;
using Orleans.Runtime.Configuration;

namespace Orleans.Runtime.Host
{
    /// <summary>
    /// A Membership Table implementation using Consul 0.6.0  https://consul.io/
    /// </summary>
    public class ConsulBasedMembershipTable : IMembershipTable, IGatewayListProvider
    {
        //Consul does not support the extended Membership Protocol and will always return the same table version information
        private readonly TableVersion _tableVersion = new TableVersion(0, "0");

        private Logger _logger;
        private ConsulClient _consulClient = null;
        private String _deploymentId;
        private String _connectionString;

        private TimeSpan _maxStaleness;

        public TimeSpan MaxStaleness
        {
            get { return _maxStaleness; }
        }

        public Boolean IsUpdatable
        {
            get { return true; }
        }

        public Task InitializeGatewayListProvider(ClientConfiguration config, Logger logger)
        {
            _maxStaleness = config.GatewayListRefreshPeriod;

            Init(config.DeploymentId, config.DataConnectionString, logger);

            return TaskDone.Done;
        }

        /// <summary>
        /// Initializes the Consul based membership table.
        /// </summary>
        /// <param name="config">The configuration for this instance.</param>
        /// <param name="tryInitTableVersion">Will be ignored: Consul does not support the extended Membership Protocol TableVersion</param>
        /// <param name="logger">The logger to be used by this instance</param>
        /// <returns></returns>
        /// <remarks>
        /// Consul Membership Provider does not support the extended Membership Protocol,
        /// therefore there is no MembershipTable to Initialise
        /// </remarks>
        public Task InitializeMembershipTable(GlobalConfiguration config, Boolean tryInitTableVersion, Logger logger)
        {
            Init(config.DeploymentId, config.DataConnectionString, logger);

            return TaskDone.Done;
        }

        private void Init(String deploymentId, String dataConnectionString, Logger logger)
        {
            _logger = logger;
            _deploymentId = deploymentId;
            _connectionString = dataConnectionString;

            _consulClient =
                new ConsulClient( config => config.Address = new Uri(dataConnectionString));
        }

        public async Task<MembershipTableData> ReadRow(SiloAddress siloAddress)
        {
            var siloRegistration = await GetConsulSiloRegistration(siloAddress);

            return AssembleMembershipTableData(siloRegistration);
        }

        public async Task<MembershipTableData> ReadAll()
        {
            var deploymentKVAddresses = await _consulClient.KV.List(ConsulSiloRegistrationAssembler.ParseDeploymentKVPrefix(_deploymentId));
            if (deploymentKVAddresses.Response == null)
            {
                _logger.Verbose("Could not find any silo registrations for deployment {0}.", _deploymentId);
                return new MembershipTableData(_tableVersion);
            }

            var allSiloRegistrations =
                deploymentKVAddresses.Response
                .Where(siloKV => !siloKV.Key.EndsWith(ConsulSiloRegistrationAssembler.SiloIAmAliveSuffix, StringComparison.OrdinalIgnoreCase))
                .Select(siloKV =>
                {
                    var iAmAliveKV = deploymentKVAddresses.Response.Where(kv => kv.Key.Equals(ConsulSiloRegistrationAssembler.ParseSiloIAmAliveKey(siloKV.Key), StringComparison.OrdinalIgnoreCase)).SingleOrDefault();
                    return ConsulSiloRegistrationAssembler.FromKVPairs(_deploymentId, siloKV, iAmAliveKV);
                }).ToArray();

            return AssembleMembershipTableData(allSiloRegistrations);
        }

        public async Task<Boolean> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            try
            {
                //Use "0" as the eTag then Consul KV CAS will treat the operation as an insert and return false if the KV already exiats.
                var consulSiloRegistration = ConsulSiloRegistrationAssembler.FromMembershipEntry(_deploymentId, entry, "0");
                var insertKV = ConsulSiloRegistrationAssembler.ToKVPair(consulSiloRegistration);

                var tryUpdate = await _consulClient.KV.CAS(insertKV);
                if (!tryUpdate.Response)
                {
                    _logger.Verbose("ConsulMembershipProvider failed to insert the row because a registration already exists for silo {0}.", entry.SiloAddress);
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
                var siloRegistration = ConsulSiloRegistrationAssembler.FromMembershipEntry(_deploymentId, entry, etag);
                var updateKV = ConsulSiloRegistrationAssembler.ToKVPair(siloRegistration);

                //If the KV.CAS() call returns false then the update failed
                var tryUpdate = await _consulClient.KV.CAS(updateKV);
                if (!tryUpdate.Response)
                {
                    _logger.Verbose("ConsulMembershipProvider failed the CAS check when updating the registration for silo {0}.", entry.SiloAddress);
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
            var iAmAliveKV = ConsulSiloRegistrationAssembler.ToIAmAliveKVPair(_deploymentId, entry.SiloAddress, entry.IAmAliveTime);
            await _consulClient.KV.Put(iAmAliveKV);
        }

        public async Task<IList<Uri>> GetGateways()
        {
            var membershipTableData = await ReadAll();
            if (membershipTableData == null) return new List<Uri>();

            return membershipTableData.Members.Select(e => e.Item1).
                                            Where(m => m.Status == SiloStatus.Active && m.ProxyPort != 0).
                                            Select(m =>
                                            {
                                                m.SiloAddress.Endpoint.Port = m.ProxyPort;
                                                return m.SiloAddress.ToGatewayUri();
                                            }).ToList();
        }

        public async Task DeleteMembershipTableEntries(String deploymentId)
        {
            await _consulClient.KV.DeleteTree(ConsulSiloRegistrationAssembler.ParseDeploymentKVPrefix(_deploymentId));
        }

        private async Task<ConsulSiloRegistration> GetConsulSiloRegistration(SiloAddress siloAddress)
        {
            var siloKey = ConsulSiloRegistrationAssembler.ParseDeploymentSiloKey(_deploymentId, siloAddress);
            var siloKVEntry = await _consulClient.KV.List(siloKey);
            if (siloKVEntry.Response == null) return null;

            var siloKV = siloKVEntry.Response.Single(KV => KV.Key.Equals(siloKey, StringComparison.OrdinalIgnoreCase));
            var iAmAliveKV = siloKVEntry.Response.SingleOrDefault(KV => KV.Key.Equals(ConsulSiloRegistrationAssembler.ParseSiloIAmAliveKey(siloKey), StringComparison.OrdinalIgnoreCase));

            var siloRegistration = ConsulSiloRegistrationAssembler.FromKVPairs(_deploymentId, siloKV, iAmAliveKV);

            return siloRegistration;
        }

        private MembershipTableData AssembleMembershipTableData(params ConsulSiloRegistration[] silos)
        {
            var membershipEntries = silos
                .Where(silo => silo != null)
                .Select(silo => ConsulSiloRegistrationAssembler.ToMembershipEntry(silo))
                .ToList();

            return new MembershipTableData(membershipEntries, _tableVersion);
        }
    }
}
