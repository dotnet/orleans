using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cassandra;
using Microsoft.Extensions.Options;
using Orleans.Clustering.Cassandra.Hosting;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Clustering.Cassandra;

internal sealed class CassandraClusteringTable : IMembershipTable
{
    private const string NotInitializedMessage = $"This instance has not been initialized. Ensure that {nameof(IMembershipTable.InitializeMembershipTable)} is called to initialize this instance before use.";
    private readonly ClusterOptions _clusterOptions;
    private readonly CassandraClusteringOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private ISession? _session;
    private OrleansQueries? _queries;
    private readonly string _identifier;

    public CassandraClusteringTable(IOptions<ClusterOptions> clusterOptions, IOptions<CassandraClusteringOptions> options, IServiceProvider serviceProvider)
    {
        _clusterOptions = clusterOptions.Value;
        _options = options.Value;
        _identifier = $"{_clusterOptions.ServiceId}-{_clusterOptions.ClusterId}";
        _serviceProvider = serviceProvider;
    }

    private ISession Session => _session ?? throw new InvalidOperationException(NotInitializedMessage);

    private OrleansQueries Queries => _queries ?? throw new InvalidOperationException(NotInitializedMessage);

    async Task IMembershipTable.InitializeMembershipTable(bool tryInitTableVersion)
    {
        _session = await _options.CreateSessionAsync(_serviceProvider);
        if (_session is null)
        {
            throw new InvalidOperationException($"Session created from configuration '{nameof(CassandraClusteringOptions)}' is null.");
        }

        _queries = await OrleansQueries.CreateInstance(_session);

        await _session.ExecuteAsync(_queries.EnsureTableExists());
        await _session.ExecuteAsync(_queries.EnsureIndexExists);

        if (!tryInitTableVersion)
            return;

        await _session.ExecuteAsync(await _queries.InsertMembershipVersion(_identifier));
    }

    async Task IMembershipTable.DeleteMembershipTableEntries(string clusterId)
    {
        if (string.Compare(clusterId, _clusterOptions.ClusterId, StringComparison.InvariantCultureIgnoreCase) != 0)
        {
            throw new ArgumentException(
                $"Cluster id {clusterId} does not match CassandraClusteringTable value of '{_clusterOptions.ClusterId}'.",
                nameof(clusterId));
        }

        await Session.ExecuteAsync(await Queries.DeleteMembershipTableEntries(_identifier));
    }

    async Task<bool> IMembershipTable.InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        // Prevent duplicate rows
        var existingRow = await ((IMembershipTable)this).ReadRow(entry.SiloAddress);
        if (existingRow is not null)
        {
            if (existingRow.Version.Version >= tableVersion.Version)
            {
                return false;
            }

            if (existingRow.Members.Any(m => m.Item1.SiloAddress.Equals(entry.SiloAddress)))
            {
                return false;
            }
        }

        var query = await Session.ExecuteAsync(await Queries.InsertMembership(_identifier, entry, tableVersion.Version - 1));
        return (bool)query.First()["[applied]"];
    }

    async Task<bool> IMembershipTable.UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
    {
        var query = await Session.ExecuteAsync(await Queries.UpdateMembership(_identifier, entry, tableVersion.Version - 1));
        return (bool)query.First()["[applied]"];
    }

    private static MembershipEntry? GetMembershipEntry(Row row)
    {
        if (row["start_time"] == null)
        {
            return null;
        }

        var result = new MembershipEntry
        {
            SiloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Parse((string)row["address"]), (int)row["port"]), (int)row["generation"]),
            SiloName = (string)row["silo_name"],
            HostName = (string)row["host_name"],
            Status = (SiloStatus)(int)row["status"],
            ProxyPort = (int)row["proxy_port"],
            StartTime = ((DateTimeOffset)row["start_time"]).UtcDateTime,
            IAmAliveTime = ((DateTimeOffset)row["i_am_alive_time"]).UtcDateTime
        };

        var suspectingSilos = (string)row["suspect_times"];
        if (!string.IsNullOrWhiteSpace(suspectingSilos))
        {
            result.SuspectTimes =
            [
                .. suspectingSilos.Split('|').Select(s =>
                {
                    var split = s.Split(',');
                    return new Tuple<SiloAddress, DateTime>(SiloAddress.FromParsableString(split[0]), LogFormatter.ParseDate(split[1]));
                }),
            ];
        }

        return result;
    }

    private async Task<MembershipTableData> GetMembershipTableData(RowSet rows)
    {
        var firstRow = rows.FirstOrDefault();
        if (firstRow != null)
        {
            var version = (int)firstRow["version"];

            var entries = new List<Tuple<MembershipEntry, string>>();
            foreach (var row in new[] { firstRow }.Concat(rows))
            {
                var entry = GetMembershipEntry(row);
                if (entry != null)
                {
                    entries.Add(new Tuple<MembershipEntry, string>(entry, string.Empty));
                }
            }

            return new MembershipTableData(entries, new TableVersion(version, version.ToString()));
        }
        else
        {
            var result = (await Session.ExecuteAsync(await Queries.MembershipReadVersion(_identifier))).FirstOrDefault();
            if (result is null)
            {
                return new MembershipTableData([], new TableVersion(0, "0"));
            }

            var version = (int)result["version"];
            return new MembershipTableData([], new TableVersion(version, version.ToString()));
        }
    }

    async Task<MembershipTableData> IMembershipTable.ReadAll()
    {
        return await GetMembershipTableData(await Session.ExecuteAsync(await Queries.MembershipReadAll(_identifier)));
    }

    async Task<MembershipTableData> IMembershipTable.ReadRow(SiloAddress key)
    {
        return await GetMembershipTableData(await Session.ExecuteAsync(await Queries.MembershipReadRow(_identifier, key)));
    }

    async Task IMembershipTable.UpdateIAmAlive(MembershipEntry entry)
    {
        await Session.ExecuteAsync(await Queries.UpdateIAmAliveTime(_identifier, entry));
    }

    public async Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
    {
        var allEntries =
            (await Session.ExecuteAsync(await Queries.MembershipReadAll(_identifier)))
            .Select(r => GetMembershipEntry(r))
            .Where(e => e is not null)
            .Cast<MembershipEntry>();

        foreach (var e in allEntries)
        {
            if (e is not { Status: SiloStatus.Active } && new DateTime(Math.Max(e.IAmAliveTime.Ticks, e.StartTime.Ticks)) < beforeDate)
            {
                await Session.ExecuteAsync(await Queries.DeleteMembershipEntry(_identifier, e));
            }
        }
    }
}