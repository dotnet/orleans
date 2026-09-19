using Microsoft.Accordant;
using Orleans.Runtime;
using static Orleans.Clustering.TestKit.MembershipTableTestData;

namespace Orleans.Clustering.TestKit;

internal enum MembershipOperationKind
{
    Initialize, ReadAll, ReadPresentRow, ReadAbsentRow, InsertNew, InsertDuplicate, InsertStaleTable,
    UpdateForward, UpdateStaleTable, UpdateStaleSnapshot, UpdateMissing, HeartbeatAdvance, HeartbeatRepeat,
    UpdateAfterHeartbeat, CleanupDead, StartSuccessor, DeleteCluster, ReadOtherCluster
}

internal sealed record MembershipRequest(MembershipOperationKind Kind, int Key = 1)
{
    public override string ToString() => $"{Kind}(key={Key}; table-mode={(Kind is MembershipOperationKind.InsertStaleTable or MembershipOperationKind.UpdateStaleTable or MembershipOperationKind.UpdateStaleSnapshot ? "previous" : "current")}; row-mode={(Kind == MembershipOperationKind.UpdateStaleSnapshot ? "previous" : "current")})";
}

[State]
internal partial class MembershipModelState : State
{
    public Dictionary<int, MembershipModelRecord> Rows { get; set; } = [];
    public Dictionary<int, int> TerminalGenerations { get; set; } = [];
    public int Version { get; set; }
    public int LastChangedKey { get; set; }
    public int Steps { get; set; }
    public bool Deleted { get; set; }
}

[State]
internal partial class MembershipModelRecord : State
{
    public int Key { get; set; }
    public int GenerationOffset { get; set; }
    public int Status { get; set; } = (int)SiloStatus.Created;
    public int Revision { get; set; }
    public long OwnerHeartbeatTicks { get; set; } = T0.Ticks;
    public int HeartbeatVersion { get; set; } = -1;
    public long StartTicks { get; set; } = T0.AddMinutes(-1).Ticks;
    public string HostName { get; set; } = string.Empty;
    public string SiloName { get; set; } = string.Empty;
    public int ProxyPort { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public int UpdateZone { get; set; }
    public int FaultZone { get; set; }
    public Dictionary<string, long> Suspects { get; set; } = [];

    internal MembershipEntry ToEntry(int seed)
    {
        var result = CreateEntry(Key, seed);
        result.SiloAddress = SiloAddress.New(result.SiloAddress.Endpoint.Address, result.SiloAddress.Endpoint.Port,
            result.SiloAddress.Generation + GenerationOffset);
        result.Status = (SiloStatus)Status;
        result.HostName = HostName;
        result.SiloName = SiloName;
        result.ProxyPort = ProxyPort;
        result.RoleName = RoleName;
        result.UpdateZone = UpdateZone;
        result.FaultZone = FaultZone;
        result.StartTime = new(StartTicks, DateTimeKind.Utc);
        result.IAmAliveTime = new(OwnerHeartbeatTicks, DateTimeKind.Utc);
        result.SuspectTimes = Suspects.Select(p => Tuple.Create(SiloAddress.FromParsableString(p.Key), new DateTime(p.Value, DateTimeKind.Utc))).ToList();
        return result;
    }

    internal static MembershipModelRecord New(int key, int generationOffset = 0) => new()
    {
        Key = key,
        GenerationOffset = generationOffset,
        Revision = 1,
        HostName = $"host-{key}",
        SiloName = $"silo-{key}",
        ProxyPort = 22000 + key,
        Suspects = []
    };
}

internal static class MembershipModel
{
    internal static readonly DateTime CleanupCutoff = T2.AddSeconds(1);

    internal static bool IsRejected(MembershipOperationKind kind) => kind is MembershipOperationKind.InsertDuplicate
        or MembershipOperationKind.InsertStaleTable or MembershipOperationKind.UpdateStaleTable
        or MembershipOperationKind.UpdateStaleSnapshot or MembershipOperationKind.UpdateMissing;

    internal static bool IsRead(MembershipOperationKind kind) => kind is MembershipOperationKind.ReadAll
        or MembershipOperationKind.ReadPresentRow or MembershipOperationKind.ReadAbsentRow or MembershipOperationKind.ReadOtherCluster;

    internal static bool IsCommit(MembershipOperationKind kind) => kind is MembershipOperationKind.InsertNew
        or MembershipOperationKind.UpdateForward or MembershipOperationKind.UpdateAfterHeartbeat or MembershipOperationKind.StartSuccessor;

    internal static bool IsHeartbeat(MembershipOperationKind kind)
        => kind is MembershipOperationKind.HeartbeatAdvance or MembershipOperationKind.HeartbeatRepeat;

    internal static bool CanApply(MembershipRequest request, MembershipModelState state)
    {
        if (state.Deleted) return false;
        var exists = state.Rows.TryGetValue(request.Key, out var row);
        var forward = exists && row!.Status is >= (int)SiloStatus.Created and < (int)SiloStatus.Dead;
        return request.Kind switch
        {
            MembershipOperationKind.InsertNew => !exists && !state.TerminalGenerations.ContainsKey(request.Key),
            MembershipOperationKind.InsertDuplicate or MembershipOperationKind.ReadPresentRow => exists,
            MembershipOperationKind.InsertStaleTable => state.Version > 0 && !exists && !state.TerminalGenerations.ContainsKey(request.Key),
            MembershipOperationKind.UpdateForward => forward,
            MembershipOperationKind.UpdateAfterHeartbeat => forward && row!.HeartbeatVersion == state.Version,
            MembershipOperationKind.UpdateStaleTable => forward && state.Version > 1 && state.LastChangedKey != request.Key,
            MembershipOperationKind.UpdateStaleSnapshot => forward && row!.Revision > 1,
            MembershipOperationKind.UpdateMissing => !exists && state.Rows.Count > 0,
            MembershipOperationKind.HeartbeatAdvance => forward && row!.OwnerHeartbeatTicks < T2.Ticks,
            MembershipOperationKind.HeartbeatRepeat => forward && row!.OwnerHeartbeatTicks > T0.Ticks,
            MembershipOperationKind.CleanupDead => state.Rows.Values.Any(r => r.Status == (int)SiloStatus.Dead),
            MembershipOperationKind.StartSuccessor => !exists && state.TerminalGenerations.ContainsKey(request.Key),
            MembershipOperationKind.DeleteCluster => state.Rows.Count > 0,
            MembershipOperationKind.ReadAbsentRow => !exists,
            _ => true
        };
    }

    // This transition never reads provider output or provider tokens.
    internal static void Apply(MembershipRequest request, MembershipModelState next, int cleanupVersionDelta = 0)
    {
        ClusteringTestKitDiagnostics.Require(!next.Deleted, "model history ended at DeleteCluster");
        if (IsRead(request.Kind) || IsRejected(request.Kind)) return;
        switch (request.Kind)
        {
            case MembershipOperationKind.InsertNew:
                next.Rows.Add(request.Key, MembershipModelRecord.New(request.Key));
                break;
            case MembershipOperationKind.StartSuccessor:
                var successor = MembershipModelRecord.New(request.Key, next.TerminalGenerations[request.Key] + 1);
                successor.StartTicks = T2.Ticks;
                successor.OwnerHeartbeatTicks = T2.Ticks;
                successor.SiloName += "-successor";
                successor.Suspects.Clear();
                next.Rows.Add(request.Key, successor);
                break;
            case MembershipOperationKind.UpdateForward:
            case MembershipOperationKind.UpdateAfterHeartbeat:
                var row = next.Rows[request.Key];
                row.Status++;
                row.Revision++;
                row.Suspects["127.0.0.1:11003@12"] = T0.Ticks;
                if (row.Status == (int)SiloStatus.Dead) next.TerminalGenerations[request.Key] = row.GenerationOffset;
                break;
            case MembershipOperationKind.HeartbeatAdvance:
                next.Rows[request.Key].OwnerHeartbeatTicks = next.Rows[request.Key].OwnerHeartbeatTicks == T0.Ticks ? T1.Ticks : T2.Ticks;
                next.Rows[request.Key].HeartbeatVersion = next.Version;
                break;
            case MembershipOperationKind.HeartbeatRepeat:
                next.Rows[request.Key].HeartbeatVersion = next.Version;
                break;
            case MembershipOperationKind.CleanupDead:
                // All generated timestamps precede CleanupCutoff, including owner writes and stale full-row payloads.
                var removed = next.Rows.Where(p => p.Value.Status == (int)SiloStatus.Dead).ToArray();
                ClusteringTestKitDiagnostics.Require(cleanupVersionDelta >= 0 && cleanupVersionDelta <= removed.Length,
                    $"model cleanup version: expected delta in [0,{removed.Length}], observed={cleanupVersionDelta}");
                foreach (var pair in removed)
                    next.Rows.Remove(pair.Key);
                if (cleanupVersionDelta > 0)
                {
                    next.Version += cleanupVersionDelta;
                    next.LastChangedKey = 0;
                }
                break;
            case MembershipOperationKind.DeleteCluster:
                next.Rows.Clear();
                next.Deleted = true;
                break;
        }

        next.Steps++;
        if (IsCommit(request.Kind))
        {
            next.Version++;
            next.LastChangedKey = request.Key;
        }
    }

    internal static MembershipModelState CopyState(MembershipModelState state) => new()
    {
        Version = state.Version,
        LastChangedKey = state.LastChangedKey,
        Steps = state.Steps,
        Deleted = state.Deleted,
        TerminalGenerations = new(state.TerminalGenerations),
        Rows = state.Rows.ToDictionary(p => p.Key, p => new MembershipModelRecord
        {
            Key = p.Value.Key,
            GenerationOffset = p.Value.GenerationOffset,
            Status = p.Value.Status,
            Revision = p.Value.Revision,
            OwnerHeartbeatTicks = p.Value.OwnerHeartbeatTicks,
            HeartbeatVersion = p.Value.HeartbeatVersion,
            StartTicks = p.Value.StartTicks,
            HostName = p.Value.HostName,
            SiloName = p.Value.SiloName,
            ProxyPort = p.Value.ProxyPort,
            RoleName = p.Value.RoleName,
            UpdateZone = p.Value.UpdateZone,
            FaultZone = p.Value.FaultZone,
            Suspects = new(p.Value.Suspects)
        })
    };
}

internal sealed record MembershipModelResult(string? Failure, ClusteringMembershipSnapshot? Observation = null, int Seed = 0, int VersionOrigin = 0, int CleanupVersionDelta = 0, bool HistoryDeleted = false)
{
    public override string ToString() => Failure ?? (HistoryDeleted ? "native deletion verified; history ended" : "validated complete expected model view");
}

internal sealed class MembershipBehavioralSpec : Spec<MembershipModelState>
{
    private readonly Dictionary<MembershipOperationKind, MembershipModelOperation> _operations = [];

    internal MembershipBehavioralSpec()
    {
        foreach (var kind in Enum.GetValues<MembershipOperationKind>())
        {
            var operation = new MembershipModelOperation(kind);
            _operations.Add(kind, operation);
            Add(operation);
        }
    }

    internal InputSet CreateInputSet(IEnumerable<MembershipRequest>? requests = null)
    {
        var result = new InputSet();
        foreach (var request in requests ?? Enum.GetValues<MembershipOperationKind>().SelectMany(k => new[] { new MembershipRequest(k, 1), new MembershipRequest(k, 2) }))
            result.Add(_operations[request.Kind].With(request, request.ToString()));
        return result;
    }
}

internal sealed class MembershipModelOperation(MembershipOperationKind kind)
    : Operation<MembershipRequest, MembershipModelResult, MembershipModelState>(kind.ToString())
{
    public override ExpectedOutcomes Apply(MembershipRequest request, MembershipModelState state)
    {
        var expected = MembershipModel.CopyState(state);
        MembershipModel.Apply(request, expected);
        if (request.Kind == MembershipOperationKind.CleanupDead)
        {
            var removedCount = state.Rows.Count - expected.Rows.Count;
            return Expect.That((MembershipModelResult result) => ValidateCleanup(expected, removedCount, result))
                .ThenState((result, next) => MembershipModel.Apply(request, next, result.CleanupVersionDelta),
                    () => new MembershipModelResult(null));
        }
        var expectation = Expect.That((MembershipModelResult result) => Validate(expected, result));
        return MembershipModel.IsRead(request.Kind) || MembershipModel.IsRejected(request.Kind)
            ? expectation.SameState()
            : expectation.ThenState(next => MembershipModel.Apply(request, next));
    }

    public override Task<MembershipModelResult> ExecuteAsync(TestingContext context, MembershipRequest request)
        => context.Get<MembershipModelExecutionContext>().ExecuteAsync(request);

    internal static ValidationResult ValidateCleanup(MembershipModelState expected, int removedCount, MembershipModelResult result)
    {
        if (result.CleanupVersionDelta < 0 || result.CleanupVersionDelta > removedCount)
            return ValidationResult.Invalid($"Accordant cleanup delta expected in [0,{removedCount}], observed={result.CleanupVersionDelta}");
        var committed = MembershipModel.CopyState(expected);
        committed.Version += result.CleanupVersionDelta;
        return Validate(committed, result);
    }

    private static ValidationResult Validate(MembershipModelState expected, MembershipModelResult result)
    {
        if (result.Failure is { } failure) return ValidationResult.Invalid(failure);
        if (expected.Deleted)
            return result.HistoryDeleted && result.Observation is null
                ? ValidationResult.Valid()
                : ValidationResult.Invalid("expected verified terminal deletion, without a replacement history");
        if (result.HistoryDeleted) return ValidationResult.Invalid("unexpected terminal deletion");
        if (result.Observation is not { } actual) return ValidationResult.Invalid("missing actual provider observation");
        if (actual.Version != expected.Version + result.VersionOrigin)
            return ValidationResult.Invalid($"Accordant expected integer={expected.Version + result.VersionOrigin}, observed={actual.Version}");
        if (actual.Rows.Count != expected.Rows.Count)
            return ValidationResult.Invalid($"Accordant expected rows={expected.Rows.Count}, observed={actual.Rows.Count}");
        foreach (var row in expected.Rows.Values)
        {
            var entry = MembershipEntrySnapshot.Capture(row.ToEntry(result.Seed));
            if (!actual.Rows.TryGetValue(entry.Identity, out var observed)) return ValidationResult.Invalid($"Accordant missing identity={entry.Identity}");
            if (entry.Difference(observed.Entry, complete: false) is { } difference) return ValidationResult.Invalid(difference);
        }
        return ValidationResult.Valid();
    }
}

internal sealed class MembershipModelExecutionContext
{
    private readonly MembershipTableTestFixture _fixture;
    private readonly int _seed;
    private readonly int _case;
    private readonly CancellationToken _ct;
    private readonly Action<MembershipOperationKind> _executed;
    private readonly Action<string>? _progress;
    private MembershipModelState _model = new();
    private readonly MembershipHistory _history = new();
    private readonly List<string> _prefix = [];
    private readonly Dictionary<string, ClusteringMembershipSnapshot> _previousUpdates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClusteringMembershipSnapshot> _beforeHeartbeats = new(StringComparer.Ordinal);
    private TableVersion? _previousTable;
    private int? _versionOrigin;
    private ClusteringMembershipSnapshot? _otherBaseline;

    internal MembershipModelExecutionContext(MembershipTableTestFixture fixture, int seed, int caseNumber,
        CancellationToken cancellationToken, Action<MembershipOperationKind> executed, Action<string>? progress = null)
    {
        _fixture = fixture; _seed = seed; _case = caseNumber; _ct = cancellationToken; _executed = executed;
        _progress = progress;
    }

    internal async Task<MembershipModelResult> ExecuteAsync(MembershipRequest request)
    {
        _ct.ThrowIfCancellationRequested();
        _prefix.Add(request.ToString());
        ReportProgress(request, "observe-before");
        try
        {
            ClusteringTestKitDiagnostics.Require(MembershipModel.CanApply(request, _model), $"generated illegal operation {request}");
            var writer = (MembershipModel.IsHeartbeat(request.Kind) ? request.Key % 2 == 1 : _prefix.Count % 2 == 0)
                ? _fixture.First : _fixture.Second;
            var reader = ReferenceEquals(writer, _fixture.First) ? _fixture.Second : _fixture.First;
            _otherBaseline ??= await MembershipTableTestRunner.Insert(_fixture.OtherCluster, CreateInitialEntry(1, _seed), _ct);
            var before = await MembershipTableTestRunner.Read(reader, _ct);
            _versionOrigin ??= before.Version;
            ValidateModel(before);
            var next = MembershipModel.CopyState(_model);
            MembershipModel.Apply(request, next);
            var input = _model.Rows.TryGetValue(request.Key, out var record) ? record.ToEntry(_seed) : CreateEntry(request.Key, _seed);
            var id = input.SiloAddress.ToParsableString();
            bool? outcome = null;
            var tableCandidate = before.Next();
            var rowToken = before.Rows.TryGetValue(id, out var oldRow) ? oldRow.Etag : before.Rows.Values.FirstOrDefault()?.Etag;
            if (request.Kind is MembershipOperationKind.InsertStaleTable or MembershipOperationKind.UpdateStaleTable)
                tableCandidate = _previousTable ?? throw new ClusteringConformanceException("missing real previous table candidate");
            if (request.Kind == MembershipOperationKind.UpdateStaleSnapshot)
            {
                var captured = _previousUpdates[id];
                tableCandidate = captured.Next();
                rowToken = captured.Rows[id].Etag;
            }
            if (request.Kind == MembershipOperationKind.UpdateAfterHeartbeat)
            {
                var captured = _beforeHeartbeats[id];
                tableCandidate = captured.Next();
                rowToken = captured.Rows[id].Etag;
            }
            if (MembershipModel.IsHeartbeat(request.Kind))
            {
                input = new MembershipEntry
                {
                    SiloAddress = input.SiloAddress,
                    IAmAliveTime = new(next.Rows[request.Key].OwnerHeartbeatTicks, DateTimeKind.Utc)
                };
            }
            var detail = $"table={tableCandidate}; row ETag={rowToken}; owner heartbeat={input.IAmAliveTime:O}";
            _prefix[^1] += $" [{detail}]";
            ReportProgress(request, "invoke");
            switch (request.Kind)
            {
                case MembershipOperationKind.Initialize:
                    await writer.InitializeMembershipTableAsync(true, _ct);
                    await reader.InitializeMembershipTableAsync(false, _ct);
                    break;
                case MembershipOperationKind.ReadAll:
                    MembershipTableTestRunner.Equal(before, await MembershipTableTestRunner.Read(writer, _ct));
                    break;
                case MembershipOperationKind.ReadPresentRow:
                case MembershipOperationKind.ReadAbsentRow:
                    MembershipTableTestRunner.Equal(before.Select(input.SiloAddress),
                        ClusteringMembershipSnapshot.Capture(await writer.ReadRowAsync(input.SiloAddress, _ct)));
                    break;
                case MembershipOperationKind.InsertNew:
                case MembershipOperationKind.StartSuccessor:
                    input = next.Rows[request.Key].ToEntry(_seed);
                    outcome = await writer.InsertRowAsync(input, tableCandidate, _ct);
                    break;
                case MembershipOperationKind.InsertDuplicate:
                case MembershipOperationKind.InsertStaleTable:
                    input.HostName += "-rejected";
                    outcome = await writer.InsertRowAsync(input, tableCandidate, _ct);
                    break;
                case MembershipOperationKind.UpdateForward:
                case MembershipOperationKind.UpdateAfterHeartbeat:
                    input = next.Rows[request.Key].ToEntry(_seed);
                    if (request.Kind == MembershipOperationKind.UpdateAfterHeartbeat) input.IAmAliveTime = T0;
                    outcome = await writer.UpdateRowAsync(input, rowToken!, tableCandidate, _ct);
                    break;
                case MembershipOperationKind.UpdateStaleSnapshot:
                case MembershipOperationKind.UpdateStaleTable:
                case MembershipOperationKind.UpdateMissing:
                    if (request.Kind != MembershipOperationKind.UpdateMissing) input = Forward(input);
                    input.IAmAliveTime = T2;
                    outcome = await writer.UpdateRowAsync(input, rowToken!, tableCandidate, _ct);
                    break;
                case MembershipOperationKind.HeartbeatAdvance:
                case MembershipOperationKind.HeartbeatRepeat:
                    _beforeHeartbeats[id] = before;
                    await writer.UpdateIAmAliveAsync(input, _ct);
                    break;
                case MembershipOperationKind.CleanupDead:
                    await writer.CleanupDefunctSiloEntriesAsync(new(MembershipModel.CleanupCutoff), _ct);
                    break;
                case MembershipOperationKind.DeleteCluster:
                    await _fixture.DeleteClusterAsync(writer, _fixture.ClusterId, allowRetained: false, _ct);
                    await _fixture.AssertHistoryPresentAsync(_fixture.OtherClusterId, _ct);
                    MembershipTableTestRunner.Equal(_otherBaseline, await MembershipTableTestRunner.Read(_fixture.OtherCluster, _ct));
                    _model = next;
                    _executed(request.Kind);
                    ReportProgress(request, "completed");
                    return new(null, HistoryDeleted: true);
                case MembershipOperationKind.ReadOtherCluster:
                    MembershipTableTestRunner.Equal(_otherBaseline, await MembershipTableTestRunner.Read(_fixture.OtherCluster, _ct));
                    break;
            }

            ReportProgress(request, "observe-after");
            var after = await MembershipTableTestRunner.Read(reader, _ct);
            var cleanupVersionDelta = 0;
            if (MembershipModel.IsRejected(request.Kind))
            {
                ClusteringTestKitDiagnostics.Require(outcome == false, $"expected conditional false; observed={outcome}; {detail}");
                MembershipTableTestRunner.Equal(before, after);
            }
            else if (MembershipModel.IsCommit(request.Kind))
            {
                ClusteringTestKitDiagnostics.Require(outcome == true, $"expected true commit; observed={outcome}; {detail}");
                MembershipTableTestRunner.AssertCommit(before, after, input);
                _previousTable = before.Next();
                if (before.Rows.ContainsKey(input.SiloAddress.ToParsableString()))
                    _previousUpdates[input.SiloAddress.ToParsableString()] = before;
            }
            else if (MembershipModel.IsHeartbeat(request.Kind))
            {
                MembershipTableTestRunner.AssertHeartbeat(before, after);
            }
            else if (request.Kind == MembershipOperationKind.CleanupDead)
            {
                MembershipTableTestRunner.AssertCleanup(before, after, MembershipModel.CleanupCutoff);
                cleanupVersionDelta = after.Version - before.Version;
                // Select a bounded legal outcome only after validating all row changes and tokens.
                next = MembershipModel.CopyState(_model);
                MembershipModel.Apply(request, next, cleanupVersionDelta);
                if (cleanupVersionDelta > 0) _previousTable = before.Next();
            }
            else MembershipTableTestRunner.Equal(before, after);

            _model = next;
            ValidateModel(after);
            _history.Observe(after);
            MembershipTableTestRunner.Equal(after, await MembershipTableTestRunner.Read(writer, _ct));
            MembershipTableTestRunner.Equal(_otherBaseline, await MembershipTableTestRunner.Read(_fixture.OtherCluster, _ct));
            _executed(request.Kind);
            ReportProgress(request, "completed");
            return new(null, after, _seed, _versionOrigin.Value, cleanupVersionDelta);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return new(ClusteringTestKitDiagnostics.FormatHistory(_seed, _case, _prefix,
                $"provider={_fixture.ProviderName}; cluster={_fixture.ClusterId}; handles=A1/A2/B1; {exception.GetType().Name}: {exception.Message}"));
        }
    }

    private void ReportProgress(MembershipRequest request, string phase)
        => _progress?.Invoke($"seed={_seed}; case={_case}; step={_prefix.Count}; operation={request}; phase={phase}");

    private void ValidateModel(ClusteringMembershipSnapshot actual)
    {
        ClusteringTestKitDiagnostics.Require(actual.Version == _versionOrigin + _model.Version,
            $"model version expected={_versionOrigin + _model.Version}, observed={actual.Version}");
        ClusteringTestKitDiagnostics.Require(actual.Rows.Count == _model.Rows.Count,
            $"model cardinality expected={_model.Rows.Count}, observed={actual.Rows.Count}");
        foreach (var row in _model.Rows.Values)
        {
            var expected = MembershipEntrySnapshot.Capture(row.ToEntry(_seed));
            ClusteringTestKitDiagnostics.Require(actual.Rows.TryGetValue(expected.Identity, out var observed), $"model identity missing={expected.Identity}");
            MembershipTableTestRunner.EqualRow(expected, observed!.Entry);
        }
    }
}
