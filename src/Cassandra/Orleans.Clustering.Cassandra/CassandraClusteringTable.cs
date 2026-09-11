using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using Microsoft.Extensions.Options;
using Orleans.Clustering.Cassandra.Hosting;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Clustering.Cassandra;

internal sealed class CassandraClusteringTable : IMembershipTable, IDisposable
{
    private const string NotInitializedMessage = $"This instance has not been initialized. Ensure that {nameof(IMembershipTable.InitializeMembershipTableAsync)} is called to initialize this instance before use.";
    private readonly ClusterOptions _clusterOptions;
    private readonly CassandraClusteringOptions _options;
    private readonly int? _ttlSeconds;
    private readonly IServiceProvider _serviceProvider;
    private ISession? _session;
    private bool _ownsSession;
    private OrleansQueries? _queries;
    private readonly string _identifier;

    public CassandraClusteringTable(
        IOptions<ClusterOptions> clusterOptions,
        IOptions<CassandraClusteringOptions> options,
        IOptions<ClusterMembershipOptions> clusterMembershipOptions,
        IServiceProvider serviceProvider)
    {
        _clusterOptions = clusterOptions.Value;
        _options = options.Value;
        _identifier = $"{_clusterOptions.ServiceId}-{_clusterOptions.ClusterId}";
        _serviceProvider = serviceProvider;
        _ttlSeconds = _options.GetCassandraTtlSeconds(clusterMembershipOptions.Value);
    }

    private OrleansQueries Queries => _queries ?? throw new InvalidOperationException(NotInitializedMessage);

    [Obsolete("Use InitializeMembershipTableAsync instead.")]
    Task IMembershipTable.InitializeMembershipTable(bool tryInitTableVersion) => InitializeMembershipTableAsync(tryInitTableVersion, CancellationToken.None);

    public async Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ownsSession = _options.OwnsSession;
        var creation = _options.CreateSessionAsync(_serviceProvider);
        ISession session;
        try
        {
            session = await OrleansQueries.AwaitAsync(creation, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (ownsSession)
            {
                DisposeAbandonedSessionAsync(creation).Ignore();
            }

            throw;
        }

        if (session is null)
        {
            throw new InvalidOperationException($"Session created from configuration '{nameof(CassandraClusteringOptions)}' is null.");
        }

        var initialized = false;
        try
        {
            var queries = await OrleansQueries.CreateInstance(session);
            await queries.EnsureTableExistsAsync(_options.InitializeRetryMaxDelay, _ttlSeconds, cancellationToken);

            if (tryInitTableVersion)
            {
                await queries.EnsureClusterVersionExistsAsync(_options.InitializeRetryMaxDelay, _identifier, cancellationToken);
            }

            _session = session;
            _ownsSession = ownsSession;
            _queries = queries;
            initialized = true;
        }
        finally
        {
            if (!initialized && ownsSession)
            {
                session.Cluster.Dispose();
            }
        }
    }

    private static async Task DisposeAbandonedSessionAsync(Task<ISession> creation)
    {
        var session = await creation.ConfigureAwait(false);
        session?.Cluster.Dispose();
    }

    public void Dispose()
    {
        var session = Interlocked.Exchange(ref _session, null);
        _queries = null;
        if (_ownsSession)
        {
            session?.Cluster.Dispose();
        }
    }

    [Obsolete("Use DeleteMembershipTableEntriesAsync instead.")]
    Task IMembershipTable.DeleteMembershipTableEntries(string clusterId) => DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

    public async Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Compare(clusterId, _clusterOptions.ClusterId, StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new ArgumentException(
                $"Cluster id {clusterId} does not match CassandraClusteringTable value of '{_clusterOptions.ClusterId}'.",
                nameof(clusterId));
        }

        await Queries.ExecuteAsync(await Queries.DeleteMembershipTableEntries(_identifier, cancellationToken), cancellationToken);
    }

    [Obsolete("Use InsertRowAsync instead.")]
    Task<bool> IMembershipTable.InsertRow(MembershipEntry entry, TableVersion tableVersion) => InsertRowAsync(entry, tableVersion, CancellationToken.None);

    public async Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken = default)
    {
        // Prevent duplicate rows
        cancellationToken.ThrowIfCancellationRequested();
        var existingRow = await ReadRowAsync(entry.SiloAddress, cancellationToken);
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

        var query = await Queries.ExecuteAsync(await Queries.InsertMembership(_identifier, entry, tableVersion.Version - 1, cancellationToken), cancellationToken);
        return (bool)query.First()["[applied]"];
    }

    [Obsolete("Use UpdateRowAsync instead.")]
    Task<bool> IMembershipTable.UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRowAsync(entry, etag, tableVersion, CancellationToken.None);

    public async Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = await Queries.ExecuteAsync(await Queries.UpdateMembership(_identifier, entry, tableVersion.Version - 1, cancellationToken), cancellationToken);
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

    private async Task<MembershipTableData> GetMembershipTableData(RowSet rows, CancellationToken cancellationToken)
    {
        int? version = null;
        var entries = new List<Tuple<MembershipEntry, string>>();
        await foreach (var row in OrleansQueries.ReadRowsAsync(rows, cancellationToken))
        {
            version ??= (int)row["version"];
            var entry = GetMembershipEntry(row);
            if (entry != null)
            {
                entries.Add(new Tuple<MembershipEntry, string>(entry, string.Empty));
            }
        }

        if (version.HasValue)
        {
            return new MembershipTableData(entries, new TableVersion(version.Value, version.Value.ToString()));
        }
        else
        {
            var result = await OrleansQueries.ReadFirstRowAsync(
                await Queries.ExecuteAsync(await Queries.MembershipReadVersion(_identifier, cancellationToken), cancellationToken),
                cancellationToken);
            if (result is null)
            {
                return new MembershipTableData([], new TableVersion(0, "0"));
            }

            var tableVersion = (int)result["version"];
            return new MembershipTableData([], new TableVersion(tableVersion, tableVersion.ToString()));
        }
    }

    [Obsolete("Use ReadAllAsync instead.")]
    Task<MembershipTableData> IMembershipTable.ReadAll() => ReadAllAsync(CancellationToken.None);

    public async Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await GetMembershipTableData(await Queries.ExecuteAsync(await Queries.MembershipReadAll(_identifier, cancellationToken), cancellationToken), cancellationToken);
    }

    [Obsolete("Use ReadRowAsync instead.")]
    Task<MembershipTableData> IMembershipTable.ReadRow(SiloAddress key) => ReadRowAsync(key, CancellationToken.None);

    public async Task<MembershipTableData> ReadRowAsync(SiloAddress key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await GetMembershipTableData(await Queries.ExecuteAsync(await Queries.MembershipReadRow(_identifier, key, cancellationToken), cancellationToken), cancellationToken);
    }

    [Obsolete("Use UpdateIAmAliveAsync instead.")]
    Task IMembershipTable.UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);

    public async Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_ttlSeconds.HasValue)
        {
            // User has opted in to Cassandra TTL behavior for membership table rows, which means the entire row's data
            // has to be written back so each cell's TTL can be updated
            MembershipTableData existingRow = await ReadRowAsync(entry.SiloAddress, cancellationToken);

            await Queries.ExecuteAsync(await Queries.UpdateIAmAliveTimeWithTtL(
                clusterIdentifier: _identifier,
                // The MembershipEntry given to this method by Orleans only contains the SiloAddress and new IAmAliveTime
                iAmAliveEntry: entry,
                existingEntry: existingRow.Members[0].Item1,
                existingVersion: existingRow.Version,
                cancellationToken: cancellationToken), cancellationToken);
        }
        else
        {
            await Queries.ExecuteAsync(await Queries.UpdateIAmAliveTime(_identifier, entry, cancellationToken), cancellationToken);
        }
    }

    [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

    public async Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await Queries.ExecuteAsync(await Queries.MembershipReadAll(_identifier, cancellationToken), cancellationToken);

        await foreach (var row in OrleansQueries.ReadRowsAsync(rows, cancellationToken))
        {
            var e = GetMembershipEntry(row);
            if (e is null)
            {
                continue;
            }

            if (e is not { Status: SiloStatus.Active } && new DateTime(Math.Max(e.IAmAliveTime.Ticks, e.StartTime.Ticks), DateTimeKind.Utc) < beforeDate)
            {
                await Queries.ExecuteAsync(await Queries.DeleteMembershipEntry(_identifier, e, cancellationToken), cancellationToken);
            }
        }
    }
}
