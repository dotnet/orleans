using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Configuration;
using System.Linq;
using Microsoft.Extensions.Options;

namespace Orleans.Clustering.Redis;

internal sealed class RedisGatewayListProvider(RedisMembershipTable table, IOptions<GatewayOptions> options) : IGatewayListProvider
{
    private readonly RedisMembershipTable _table = table;
    private readonly GatewayOptions _gatewayOptions = options.Value;

    public TimeSpan MaxStaleness => _gatewayOptions.GatewayListRefreshPeriod;

    public bool IsUpdatable => true;

    public Task<IList<Uri>> GetGateways() => GetGatewaysAsync(CancellationToken.None);

    public async Task<IList<Uri>> GetGatewaysAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_table.IsInitialized)
        {
            await _table.InitializeMembershipTableAsync(true, cancellationToken);
        }

        var all = await _table.ReadAllAsync(cancellationToken);
        var result = all.Members
           .Where(x => x.Item1.Status == SiloStatus.Active && x.Item1.ProxyPort != 0)
           .Select(x =>
            {
                var entry = x.Item1;
                return SiloAddress.New(entry.SiloAddress.Endpoint.Address, entry.ProxyPort, entry.SiloAddress.Generation).ToGatewayUri();
            }).ToList();
        return result;
    }

    public Task InitializeGatewayListProvider() => InitializeGatewayListProviderAsync(CancellationToken.None);

    public async Task InitializeGatewayListProviderAsync(CancellationToken cancellationToken = default)
    {
        await _table.InitializeMembershipTableAsync(true, cancellationToken);
    }
}
