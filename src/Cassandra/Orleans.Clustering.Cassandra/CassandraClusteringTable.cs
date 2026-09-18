using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private ISession? _session;
    private bool _ownsSession;
    private bool _disposed;
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
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            await InitializeMembershipTableCoreAsync(tryInitTableVersion, cancellationToken);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task InitializeMembershipTableCoreAsync(bool tryInitTableVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = _session;
        var ownsSession = _ownsSession;
        var isNewSession = session is null;
        if (session is null)
        {
            ownsSession = _options.OwnsSession;
            var creation = _options.CreateSessionAsync(_serviceProvider);
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
        }

        if (session is null)
        {
            throw new InvalidOperationException($"Session created from configuration '{nameof(CassandraClusteringOptions)}' is null.");
        }

        var initialized = false;
        try
        {
            var queries = isNewSession ? await OrleansQueries.CreateInstance(session, _ttlSeconds) : Queries;
            await queries.EnsureTableExistsAsync(_options.InitializeRetryMaxDelay, cancellationToken);

            if (tryInitTableVersion)
            {
                await queries.EnsureClusterVersionExistsAsync(_options.InitializeRetryMaxDelay, _identifier, cancellationToken);
            }

            lock (_initializationLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (isNewSession)
                {
                    _session = session;
                    _ownsSession = ownsSession;
                    _queries = queries;
                }

                initialized = true;
            }
        }
        finally
        {
            if (!initialized && isNewSession && ownsSession)
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
        ISession? session;
        lock (_initializationLock)
        {
            _disposed = true;
            session = _ownsSession ? _session : null;
            _session = null;
            _queries = null;
        }

        session?.Cluster.Dispose();
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
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetExpectedVersion(tableVersion, out var version))
        {
            return false;
        }

        var query = await Queries.ExecuteAsync(await Queries.InsertMembership(
            _identifier, entry, version, cancellationToken), cancellationToken);
        return (bool)query.First()["[applied]"];
    }

    [Obsolete("Use UpdateRowAsync instead.")]
    Task<bool> IMembershipTable.UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) => UpdateRowAsync(entry, etag, tableVersion, CancellationToken.None);

    public async Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetExpectedVersion(tableVersion, out var expectedVersion) || !string.Equals(etag, tableVersion.VersionEtag, StringComparison.Ordinal))
        {
            return false;
        }

        while (true)
        {
            var current = await ReadRowForUpdateAsync(entry.SiloAddress, cancellationToken);
            if (current is not { } row || row.Version != expectedVersion)
            {
                return false;
            }

            var query = await Queries.ExecuteAsync(await Queries.UpdateMembership(
                _identifier, entry, row.Version, row.Entry.IAmAliveTime, cancellationToken), cancellationToken);
            if ((bool)query.First()["[applied]"])
            {
                return true;
            }

            // Only heartbeat races can be retried with the caller's original table version.
        }
    }

    private static bool TryGetExpectedVersion(TableVersion tableVersion, out int version) =>
        int.TryParse(tableVersion.VersionEtag, NumberStyles.None, CultureInfo.InvariantCulture, out version)
        && version >= 0
        && version < int.MaxValue
        && tableVersion.Version == version + 1
        && tableVersion.VersionEtag == version.ToString(CultureInfo.InvariantCulture);

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
        if (suspectingSilos is not null)
        {
            result.SuspectTimes = suspectingSilos.Length == 0 ? [] :
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
                entries.Add(new Tuple<MembershipEntry, string>(entry, version.Value.ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (version.HasValue)
        {
            return new MembershipTableData(entries, new TableVersion(version.Value, version.Value.ToString(CultureInfo.InvariantCulture)));
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
            return new MembershipTableData([], new TableVersion(tableVersion, tableVersion.ToString(CultureInfo.InvariantCulture)));
        }
    }

    [Obsolete("Use ReadAllAsync instead.")]
    Task<MembershipTableData> IMembershipTable.ReadAll() => ReadAllAsync(CancellationToken.None);

    public async Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        return await ReadConsistentAsync(null, cancellationToken);
    }

    [Obsolete("Use ReadRowAsync instead.")]
    Task<MembershipTableData> IMembershipTable.ReadRow(SiloAddress key) => ReadRowAsync(key, CancellationToken.None);

    public async Task<MembershipTableData> ReadRowAsync(SiloAddress key, CancellationToken cancellationToken = default)
    {
        return await ReadConsistentAsync(key, cancellationToken);
    }

    private async Task<MembershipTableData> ReadConsistentAsync(SiloAddress? key, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = await ReadVersionAsync(cancellationToken);
            var statement = key is null
                ? await Queries.MembershipReadAll(_identifier, cancellationToken)
                : await Queries.MembershipReadRow(_identifier, key, cancellationToken);
            var result = await GetMembershipTableData(await Queries.ExecuteAsync(statement, cancellationToken), cancellationToken);
            var after = await ReadVersionAsync(cancellationToken);
            // Cassandra paging does not retain a snapshot. Fence the entire enumeration, not just the first page.
            if (before == after && result.Version.Version == after)
            {
                return result;
            }
        }
    }

    private async Task<int> ReadVersionAsync(CancellationToken cancellationToken)
    {
        var rows = await Queries.ExecuteAsync(await Queries.MembershipReadVersion(_identifier, cancellationToken), cancellationToken);
        var row = await OrleansQueries.ReadFirstRowAsync(rows, cancellationToken);
        return row is null ? 0 : (int)row["version"];
    }

    private async Task<(MembershipEntry Entry, int Version)?> ReadRowForUpdateAsync(SiloAddress key, CancellationToken cancellationToken)
    {
        // A single-row serial read supplies the row/version pair; the subsequent CAS fences concurrent changes.
        var rows = await Queries.ExecuteAsync(await Queries.MembershipReadRow(_identifier, key, cancellationToken), cancellationToken);
        var row = await OrleansQueries.ReadFirstRowAsync(rows, cancellationToken);
        if (row is null || GetMembershipEntry(row) is not { } entry)
        {
            return null;
        }

        return (entry, (int)row["version"]);
    }

    [Obsolete("Use UpdateIAmAliveAsync instead.")]
    Task IMembershipTable.UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);

    public async Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_ttlSeconds.HasValue)
        {
            await Queries.ExecuteAsync(await Queries.UpdateIAmAliveTime(_identifier, entry, cancellationToken), cancellationToken);
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await ReadRowForUpdateAsync(entry.SiloAddress, cancellationToken);
            if (current is not { } row || row.Entry.IAmAliveTime >= entry.IAmAliveTime)
            {
                return;
            }

            var statement = await Queries.UpdateIAmAliveTimeWithTtl(_identifier, entry, row.Entry, row.Version, cancellationToken);
            var result = await Queries.ExecuteAsync(statement, cancellationToken);
            if ((bool)result.First()["[applied]"])
            {
                return;
            }
        }
    }

    [Obsolete("Use CleanupDefunctSiloEntriesAsync instead.")]
    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) => CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

    public async Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken = default)
    {
        var table = await ReadAllAsync(cancellationToken);
        foreach (var row in table.Members)
        {
            var entry = row.Item1;
            if (entry.Status == SiloStatus.Dead
                && Math.Max(entry.IAmAliveTime.Ticks, entry.StartTime.Ticks) < beforeDate.UtcDateTime.Ticks
                && entry.SuspectTimes?.Any(vote => vote.Item2 >= beforeDate.UtcDateTime) != true)
            {
                await Queries.ExecuteAsync(await Queries.DeleteMembershipEntry(_identifier, entry, cancellationToken), cancellationToken);
            }
        }
    }
}
