using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cassandra;
using Microsoft.Extensions.Options;
using Orleans.Clustering.Cassandra.Hosting;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.Clustering.Cassandra;

internal sealed class CassandraGatewayListProvider : IGatewayListProvider
{
    private const string NotInitializedMessage = $"This instance has not been initialized. Ensure that {nameof(IGatewayListProvider.InitializeGatewayListProvider)} is called to initialize this instance before use.";
    private readonly TimeSpan _maxStaleness;
    private readonly string _identifier;
    private readonly CassandraClusteringOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly int? _ttlSeconds;
    private ISession? _session;
    private OrleansQueries? _queries;
    private DateTime _cacheUntil;
    private List<Uri>? _cachedResult;

    TimeSpan IGatewayListProvider.MaxStaleness => _maxStaleness;

    bool IGatewayListProvider.IsUpdatable => true;

    public CassandraGatewayListProvider(
        IOptions<ClusterOptions> clusterOptions,
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<CassandraClusteringOptions> options,
        IOptions<ClusterMembershipOptions> clusterMembershipOptions,
        IServiceProvider serviceProvider)
    {
        _identifier = $"{clusterOptions.Value.ServiceId}-{clusterOptions.Value.ClusterId}";
        _options = options.Value;
        _serviceProvider = serviceProvider;

        _maxStaleness = gatewayOptions.Value.GatewayListRefreshPeriod;
        _ttlSeconds = _options.GetCassandraTtlSeconds(clusterMembershipOptions.Value);
    }

    private ISession Session => _session ?? throw new InvalidOperationException(NotInitializedMessage);

    private OrleansQueries Queries => _queries ?? throw new InvalidOperationException(NotInitializedMessage);

    async Task IGatewayListProvider.InitializeGatewayListProvider()
    {
        _session = await _options.CreateSessionAsync(_serviceProvider);
        if (_session is null)
        {
            throw new InvalidOperationException($"Session created from configuration '{nameof(CassandraClusteringOptions)}' is null.");
        }

        _queries = await OrleansQueries.CreateInstance(_session);

        if (await DoesTableAlreadyExistAsync())
        {
            return;
        }

        try
        {
            await MakeTableAsync(true);
        }
        catch (WriteTimeoutException) // If there's contention on table creation, backoff a bit and try once more
        {
            // Randomize the delay to avoid contention, preferring that more instances will wait longer
            var nextSingle = Random.Shared.NextSingle();
            await Task.Delay(TimeSpan.FromSeconds(10) * Math.Sqrt(nextSingle));

            if (await DoesTableAlreadyExistAsync())
            {
                return;
            }

            await MakeTableAsync(true);
        }
    }



    private async Task MakeTableAsync(bool tryInitTableVersion)
    {
        await Session.ExecuteAsync(Queries.EnsureTableExists(_ttlSeconds));
        await Session.ExecuteAsync(Queries.EnsureIndexExists);

        if (!tryInitTableVersion)
            return;

        await Session.ExecuteAsync(await Queries.InsertMembershipVersion(_identifier));
    }

    private async Task<bool> DoesTableAlreadyExistAsync()
    {
        try
        {
            var resultSet = await Session.ExecuteAsync(Queries.CheckIfTableExists(Session.Keyspace, ConsistencyLevel.LocalOne));
            return resultSet.Any();
        }
        catch (UnavailableException)
        {
            var resultSet = await Session.ExecuteAsync(Queries.CheckIfTableExists(Session.Keyspace, ConsistencyLevel.One));
            return resultSet.Any();
        }
        catch (UnauthorizedException)
        {
            return false;
        }
    }

    async Task<IList<Uri>> IGatewayListProvider.GetGateways()
    {
        if (_cachedResult is not null && _cacheUntil > DateTime.UtcNow)
        {
            return [.. _cachedResult];
        }

        var rows = await Session.ExecuteAsync(await Queries.GatewaysQuery(_identifier, (int)SiloStatus.Active));
        var result = new List<Uri>();

        foreach (var row in rows)
        {
            result.Add(SiloAddress.New(new IPEndPoint(IPAddress.Parse((string)row["address"]), (int)row["proxy_port"]), (int)row["generation"]).ToGatewayUri());
        }

        _cacheUntil = DateTime.UtcNow + _maxStaleness;
        _cachedResult = result;
        return [.. result];
    }
}