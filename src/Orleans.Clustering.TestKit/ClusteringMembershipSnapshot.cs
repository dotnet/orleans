using System.Collections.Immutable;
using System.Globalization;
using Orleans.Runtime;

namespace Orleans.Clustering.TestKit;

internal sealed record SuspectSnapshot(string Identity, DateTime Time);

// No mutable MembershipEntry, SiloAddress endpoint, or list is kept in an observation.
internal sealed record MembershipEntrySnapshot(
    string Identity, SiloStatus Status, int ProxyPort, string HostName, string SiloName,
    string? RoleName, int UpdateZone, int FaultZone, DateTime StartTime, DateTime IAmAliveTime,
    ImmutableArray<SuspectSnapshot> Suspects)
{
    internal static MembershipEntrySnapshot Capture(MembershipEntry entry) => new(
        entry.SiloAddress.ToParsableString(), entry.Status, entry.ProxyPort, entry.HostName, entry.SiloName,
        entry.RoleName ?? string.Empty, entry.UpdateZone, entry.FaultZone, entry.StartTime, entry.IAmAliveTime,
        (entry.SuspectTimes ?? []).Select(v => new SuspectSnapshot(v.Item1.ToParsableString(), v.Item2))
            .OrderBy(v => v.Identity, StringComparer.Ordinal).ThenBy(v => v.Time).ToImmutableArray());

    internal MembershipEntry ToEntry() => new()
    {
        SiloAddress = SiloAddress.FromParsableString(Identity), Status = Status, ProxyPort = ProxyPort,
        HostName = HostName, SiloName = SiloName, RoleName = RoleName, UpdateZone = UpdateZone,
        FaultZone = FaultZone, StartTime = StartTime, IAmAliveTime = IAmAliveTime,
        SuspectTimes = Suspects.Select(v => Tuple.Create(SiloAddress.FromParsableString(v.Identity), v.Time)).ToList()
    };

    internal DateTime GetEffectiveUpdateTime()
        => Suspects.Select(v => v.Time).Append(StartTime).Append(IAmAliveTime).Max();

    internal string? Difference(MembershipEntrySnapshot actual, bool complete)
    {
        // Compare values, not display strings. Capture normalizes the absent optional role name.
        if (Identity != actual.Identity) return Field(nameof(Identity), Identity, actual.Identity);
        if (Status != actual.Status) return Field(nameof(Status), Status, actual.Status);
        if (ProxyPort != actual.ProxyPort) return Field(nameof(ProxyPort), ProxyPort, actual.ProxyPort);
        if (HostName != actual.HostName) return Field(nameof(HostName), HostName, actual.HostName);
        if (SiloName != actual.SiloName) return Field(nameof(SiloName), SiloName, actual.SiloName);
        if (RoleName != actual.RoleName) return Field(nameof(RoleName), RoleName, actual.RoleName);
        if (UpdateZone != actual.UpdateZone) return Field(nameof(UpdateZone), UpdateZone, actual.UpdateZone);
        if (FaultZone != actual.FaultZone) return Field(nameof(FaultZone), FaultZone, actual.FaultZone);
        if (StartTime != actual.StartTime) return Field(nameof(StartTime), StartTime, actual.StartTime);
        if (!Suspects.SequenceEqual(actual.Suspects)) return Field("SuspectTimes", string.Join(",", Suspects), string.Join(",", actual.Suspects));
        if (complete && IAmAliveTime != actual.IAmAliveTime) return Field(nameof(IAmAliveTime), IAmAliveTime, actual.IAmAliveTime);
        return null;
    }

    private string Field(string field, object? expected, object? actual)
        => $"{Identity}.{field}: expected={Format(expected)}, observed={Format(actual)}";

    private static string Format(object? value)
        => value is DateTime time ? time.ToString("O", CultureInfo.InvariantCulture) : value?.ToString() ?? "<null>";
}

internal sealed record MembershipRowSnapshot(MembershipEntrySnapshot Entry, string Etag);

internal sealed record ClusteringMembershipSnapshot(int Version, string TableEtag, ImmutableDictionary<string, MembershipRowSnapshot> Rows)
{
    internal static ClusteringMembershipSnapshot Capture(MembershipTableData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var rows = ImmutableDictionary.CreateBuilder<string, MembershipRowSnapshot>(StringComparer.Ordinal);
        foreach (var (entry, etag) in data.Members)
        {
            var detached = MembershipEntrySnapshot.Capture(entry);
            ClusteringTestKitDiagnostics.Require(!rows.ContainsKey(detached.Identity), $"duplicate observed identity={detached.Identity}");
            rows.Add(detached.Identity, new(detached, etag));
        }

        return new(data.Version.Version, data.Version.VersionEtag, rows.ToImmutable());
    }

    internal TableVersion Next() => new TableVersion(Version, TableEtag).Next();
    internal MembershipRowSnapshot Row(SiloAddress address) => Rows[address.ToParsableString()];
    internal ClusteringMembershipSnapshot Select(SiloAddress address)
        => this with { Rows = Rows.Where(p => p.Key == address.ToParsableString()).ToImmutableDictionary(StringComparer.Ordinal) };

    internal string? CompareComplete(ClusteringMembershipSnapshot actual) => Compare(actual, true);
    internal string? CompareVersioned(ClusteringMembershipSnapshot actual) => Compare(actual, false);

    internal string? CompareSameVersionSuccessor(ClusteringMembershipSnapshot actual)
    {
        var compacted = Rows.Where(pair => pair.Value.Entry.Status == SiloStatus.Dead && !actual.Rows.ContainsKey(pair.Key))
            .Select(pair => pair.Key);
        return (this with { Rows = Rows.RemoveRange(compacted) }).CompareVersioned(actual);
    }

    private string? Compare(ClusteringMembershipSnapshot actual, bool complete)
    {
        if (Version != actual.Version) return $"table integer: expected={Version}, observed={actual.Version}";
        if (TableEtag != actual.TableEtag) return $"table ETag: expected={TableEtag}, observed={actual.TableEtag}";
        foreach (var (identity, row) in Rows)
        {
            if (!actual.Rows.TryGetValue(identity, out var observed))
            {
                return $"missing identity={identity}, expected status={row.Entry.Status}";
            }

            if (row.Entry.Difference(observed.Entry, complete) is { } difference) return difference;
            if (complete && row.Etag != observed.Etag) return $"{identity}.row ETag: expected={row.Etag}, observed={observed.Etag}";
        }

        var extra = actual.Rows.Keys.Except(Rows.Keys).FirstOrDefault();
        return extra is null ? null : $"unexpected identity={extra}";
    }
}

internal sealed class MembershipHistory
{
    private ClusteringMembershipSnapshot? _previous;
    private readonly HashSet<string> _terminal = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _maxHeartbeats = new(StringComparer.Ordinal);

    internal bool IsTerminal(string identity) => _terminal.Contains(identity);

    internal void Observe(ClusteringMembershipSnapshot observation)
    {
        if (_previous is { } prior)
        {
            ClusteringTestKitDiagnostics.Require(observation.Version >= prior.Version,
                $"history version rollback: previous={prior.Version}, observed={observation.Version}");
            if (observation.Version == prior.Version)
            {
                var difference = prior.CompareSameVersionSuccessor(observation);
                ClusteringTestKitDiagnostics.Require(difference is null, $"same-version drift: {difference}");
            }
        }

        foreach (var (identity, row) in observation.Rows)
        {
            ClusteringTestKitDiagnostics.Require(!_terminal.Contains(identity) || row.Entry.Status == SiloStatus.Dead,
                $"terminal identity reappeared live: {identity}; observed={row.Entry.Status}");
            if (_maxHeartbeats.TryGetValue(identity, out var maximum))
            {
                ClusteringTestKitDiagnostics.Require(row.Entry.IAmAliveTime >= maximum,
                    $"heartbeat maximum regressed: {identity}; expected>={maximum:O}, observed={row.Entry.IAmAliveTime:O}");
            }
        }

        if (_previous is { } previous)
            _terminal.UnionWith(previous.Rows.Keys.Except(observation.Rows.Keys));
        foreach (var (identity, row) in observation.Rows)
        {
            _maxHeartbeats[identity] = row.Entry.IAmAliveTime;
            if (row.Entry.Status == SiloStatus.Dead) _terminal.Add(identity);
        }
        _previous = observation;
    }
}
