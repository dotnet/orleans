using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Configuration;
using System.Linq;
using Microsoft.Extensions.Options;

namespace Orleans.Clustering.Redis
{
    internal class RedisGatewayListProvider : IGatewayListProvider
    {
        private readonly RedisMembershipTable _table;
        private readonly GatewayOptions _gatewayOptions;

        public RedisGatewayListProvider(RedisMembershipTable table, IOptions<GatewayOptions> options)
        {
            _gatewayOptions = options.Value;
            _table = table;
        }

        public TimeSpan MaxStaleness => _gatewayOptions.GatewayListRefreshPeriod;

        public bool IsUpdatable => true;

        public async Task<IList<Uri>> GetGateways()
        {
            if (!_table.IsInitialized)
            {
                await _table.InitializeMembershipTable(true);
            }

            var all = await _table.ReadAll();
            var result = all.Members
               .Where(x => x.Item1.Status == SiloStatus.Active && x.Item1.ProxyPort != 0)
               .Select(x =>
                {
                    x.Item1.SiloAddress.Endpoint.Port = x.Item1.ProxyPort;
                    return x.Item1.SiloAddress.ToGatewayUri();
                }).ToList();
            return result;
        }

        public async Task InitializeGatewayListProvider()
        {
            await _table.InitializeMembershipTable(true);
        }
    }
}