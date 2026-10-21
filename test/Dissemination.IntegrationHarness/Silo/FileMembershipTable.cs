using System.Text.Json;
using Orleans.Runtime;

namespace Orleans.Dissemination.IntegrationHarness;

// The wire schema is deliberately independent of either Orleans serializer version.
// This is a single-machine CI provider, not a production storage implementation.
internal sealed class FileMembershipTable(NodeConfiguration configuration) : IMembershipTable
{
    private readonly string _dataPath = Path.Combine(configuration.MembershipDirectory, "membership.json");
    private readonly string _lockPath = Path.Combine(configuration.MembershipDirectory, "membership.lock");
    private TableState? _frozen;

    public bool ReadsFrozen => Volatile.Read(ref _frozen) is not null;

    public async Task FreezeReads(bool freeze, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = freeze ? await Access(static state => state, write: false, cancellationToken) : null;
        Volatile.Write(ref _frozen, state);
    }

#if NEW_RUNTIME
    Task IMembershipTable.InitializeMembershipTable(bool tryInitTableVersion) =>
        InitializeMembershipTableAsync(tryInitTableVersion, CancellationToken.None);

    Task IMembershipTable.DeleteMembershipTableEntries(string clusterId) =>
        DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

    Task IMembershipTable.CleanupDefunctSiloEntries(DateTimeOffset beforeDate) =>
        CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

    Task<MembershipTableData> IMembershipTable.ReadRow(SiloAddress key) => ReadRowAsync(key, CancellationToken.None);

    Task<MembershipTableData> IMembershipTable.ReadAll() => ReadAllAsync(CancellationToken.None);

    Task<bool> IMembershipTable.InsertRow(MembershipEntry entry, TableVersion tableVersion) =>
        InsertRowAsync(entry, tableVersion, CancellationToken.None);

    Task<bool> IMembershipTable.UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) =>
        UpdateRowAsync(entry, etag, tableVersion, CancellationToken.None);

    Task IMembershipTable.UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);
#else
    public Task InitializeMembershipTable(bool tryInitTableVersion) =>
        InitializeMembershipTableAsync(tryInitTableVersion, CancellationToken.None);

    public Task DeleteMembershipTableEntries(string clusterId) =>
        DeleteMembershipTableEntriesAsync(clusterId, CancellationToken.None);

    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) =>
        CleanupDefunctSiloEntriesAsync(beforeDate, CancellationToken.None);

    public Task<MembershipTableData> ReadRow(SiloAddress key) => ReadRowAsync(key, CancellationToken.None);

    public Task<MembershipTableData> ReadAll() => ReadAllAsync(CancellationToken.None);

    public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion) =>
        InsertRowAsync(entry, tableVersion, CancellationToken.None);

    public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion) =>
        UpdateRowAsync(entry, etag, tableVersion, CancellationToken.None);

    public Task UpdateIAmAlive(MembershipEntry entry) => UpdateIAmAliveAsync(entry, CancellationToken.None);
#endif

    public Task InitializeMembershipTableAsync(bool tryInitTableVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(configuration.MembershipDirectory);
        return Access(static _ => true, write: true, cancellationToken);
    }

    public Task DeleteMembershipTableEntriesAsync(string clusterId, CancellationToken cancellationToken) => Access(state =>
    {
        if (!StringComparer.Ordinal.Equals(clusterId, configuration.ClusterId))
        {
            throw new InvalidOperationException("The harness provider cannot delete another cluster.");
        }

        state.Rows.Clear();
        state.Version++;
        return true;
    }, write: true, cancellationToken);

    public Task CleanupDefunctSiloEntriesAsync(DateTimeOffset beforeDate, CancellationToken cancellationToken) => Access(state =>
    {
        foreach (var key in state.Rows.Where(pair =>
            pair.Value.Entry.Status == (int)SiloStatus.Dead
            && pair.Value.Entry.IAmAliveTime < beforeDate.UtcDateTime).Select(pair => pair.Key).ToArray())
        {
            state.Rows.Remove(key);
        }

        return true;
    }, write: true, cancellationToken);

    public async Task<MembershipTableData> ReadRowAsync(SiloAddress key, CancellationToken cancellationToken)
    {
        var all = await ReadAllAsync(cancellationToken);
        return new MembershipTableData(all.Members.Where(row => row.Item1.SiloAddress.Equals(key)).ToList(), all.Version);
    }

    public async Task<MembershipTableData> ReadAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Volatile.Read(ref _frozen) ?? await Access(static state => state, write: false, cancellationToken);
        return new MembershipTableData(
            state.Rows.Values.Select(row => Tuple.Create(row.Entry.ToEntry(), row.Etag)).ToList(),
            new TableVersion(state.Version, state.Etag));
    }

    public Task<bool> InsertRowAsync(MembershipEntry entry, TableVersion tableVersion, CancellationToken cancellationToken) => Access(state =>
    {
        var key = entry.SiloAddress.ToParsableString();
        if (state.Rows.ContainsKey(key) || !StringComparer.Ordinal.Equals(tableVersion.VersionEtag, state.Etag))
        {
            return false;
        }

        state.Rows.Add(key, new Row(EntryData.From(entry), Guid.NewGuid().ToString("N")));
        state.Version = tableVersion.Version;
        state.Etag = Guid.NewGuid().ToString("N");
        return true;
    }, write: true, cancellationToken);

    public Task<bool> UpdateRowAsync(MembershipEntry entry, string etag, TableVersion tableVersion, CancellationToken cancellationToken) => Access(state =>
    {
        var key = entry.SiloAddress.ToParsableString();
        if (!state.Rows.TryGetValue(key, out var row)
            || !StringComparer.Ordinal.Equals(row.Etag, etag)
            || !StringComparer.Ordinal.Equals(tableVersion.VersionEtag, state.Etag))
        {
            return false;
        }

        state.Rows[key] = new Row(EntryData.From(entry), Guid.NewGuid().ToString("N"));
        state.Version = tableVersion.Version;
        state.Etag = Guid.NewGuid().ToString("N");
        return true;
    }, write: true, cancellationToken);

    public Task UpdateIAmAliveAsync(MembershipEntry entry, CancellationToken cancellationToken) => Access(state =>
    {
        if (state.Rows.TryGetValue(entry.SiloAddress.ToParsableString(), out var row))
        {
            state.Rows[entry.SiloAddress.ToParsableString()] = row with
            {
                Entry = row.Entry with { IAmAliveTime = entry.IAmAliveTime },
            };
        }

        return true;
    }, write: true, cancellationToken);

    private async Task<T> Access<T>(Func<TableState, T> action, bool write, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        FileStream lease;
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                lease = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException)
            {
                await Task.Delay(10, deadline.Token);
            }
        }

        await using (lease)
        {
            var state = File.Exists(_dataPath)
                ? JsonSerializer.Deserialize<TableState>(await File.ReadAllTextAsync(_dataPath, deadline.Token))!
                : new TableState();
            deadline.Token.ThrowIfCancellationRequested();
            var result = action(state);
            if (write)
            {
                // The sibling file is in the owned artifact directory, never the system temporary directory.
                var staging = _dataPath + ".next";
                await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(state), deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                File.Move(staging, _dataPath, overwrite: true);
            }

            return result;
        }
    }

    private sealed class TableState
    {
        public int Version { get; set; }
        public string Etag { get; set; } = "0";
        public Dictionary<string, Row> Rows { get; set; } = [];
    }

    private sealed record Row(EntryData Entry, string Etag);

    internal sealed record EntryData(
        string Address,
        int Status,
        int ProxyPort,
        string HostName,
        string SiloName,
        string? RoleName,
        int UpdateZone,
        int FaultZone,
        DateTime StartTime,
        DateTime IAmAliveTime,
        Dictionary<string, DateTime> Suspects)
    {
        public static EntryData From(MembershipEntry entry) => new(
            entry.SiloAddress.ToParsableString(), (int)entry.Status, entry.ProxyPort,
            entry.HostName, entry.SiloName, entry.RoleName, entry.UpdateZone, entry.FaultZone,
            entry.StartTime, entry.IAmAliveTime,
            entry.SuspectTimes?.ToDictionary(pair => pair.Item1.ToParsableString(), pair => pair.Item2) ?? []);

        public MembershipEntry ToEntry() => new()
        {
            SiloAddress = SiloAddress.FromParsableString(Address),
            Status = (SiloStatus)Status,
            ProxyPort = ProxyPort,
            HostName = HostName,
            SiloName = SiloName,
            RoleName = RoleName,
            UpdateZone = UpdateZone,
            FaultZone = FaultZone,
            StartTime = StartTime,
            IAmAliveTime = IAmAliveTime,
            SuspectTimes = Suspects.Select(pair => Tuple.Create(SiloAddress.FromParsableString(pair.Key), pair.Value)).ToList(),
        };
    }
}
