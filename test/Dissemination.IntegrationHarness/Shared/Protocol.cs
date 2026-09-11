using System.Text.Json;

namespace Orleans.Dissemination.IntegrationHarness;

internal sealed record NodeConfiguration(
    string Name,
    string ClusterId,
    string MembershipDirectory,
    int SiloPort,
    int ParentProcessId,
    bool Enabled,
    bool FastRecovery = true);

internal sealed record Command(
    int Id,
    string Operation,
    string? Peer = null,
    int Count = 1,
    bool Value = false,
    long Version = 0);

internal sealed record Response(int Id, NodeSnapshot? Snapshot, string? Error);

internal sealed record BinaryIdentity(
    int ProcessId,
    string Runtime,
    string SourceRevision,
    string AssemblyName,
    string AssemblyVersion,
    string InformationalVersion,
    string ModuleVersionId,
    string Sha256,
    string AssemblyPath,
    bool HasDissemination)
{
    public Dictionary<string, AssemblyProof> Assemblies { get; init; } = [];
}

internal sealed record AssemblyProof(
    string Version,
    string InformationalVersion,
    string ModuleVersionId,
    string Sha256,
    string Path);

internal sealed record MetricValue(long Count, double Sum);

internal sealed record TreeGateSnapshot(bool Blocked, int InFlight, long Admitted, long Rejected);

internal sealed record StateComparisonProbe(string Before, string After, string[] PublicFields);

internal sealed record ControlledCallSnapshot(
    string? Peer,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CancellationRequestedAtUtc,
    bool CancellationRequested,
    bool RawTaskCompleted,
    string? RawTaskStatus,
    string? RawTaskError);

// The token source outlives the start command. Cancellation is a separate, explicit phase and
// completion means the actual RPC task completed, not a wait canceled by that same token.
internal sealed class ControlledCall : IDisposable
{
    private CancellationTokenSource? _cancellation;
    private Task? _request;
    private string? _peer;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _cancelledAt;

    public ControlledCallSnapshot Snapshot => new(
        _peer,
        _startedAt,
        _cancelledAt,
        _cancellation?.IsCancellationRequested == true,
        _request?.IsCompleted == true,
        _request?.Status.ToString(),
        _request?.Exception?.ToString());

    public void Start(string peer, Func<CancellationToken, Task> invoke)
    {
        if (_request is not null)
        {
            throw new InvalidOperationException("A controlled RPC has already been started.");
        }

        _peer = peer;
        _startedAt = DateTimeOffset.UtcNow;
        _cancellation = new CancellationTokenSource();
        _request = invoke(_cancellation.Token);
        _ = _request.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task Cancel(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = _cancellation ?? throw new InvalidOperationException("No controlled RPC has been started.");
        var request = _request ?? throw new InvalidOperationException("The controlled RPC was not created.");
        if (request.IsCompleted)
        {
            throw new InvalidOperationException($"The RPC completed before explicit cancellation: {request.Status}.", request.Exception);
        }

        _cancelledAt = DateTimeOffset.UtcNow;
        await source.CancelAsync().WaitAsync(cancellationToken);
        try
        {
            await request.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return;
        }

        throw new InvalidOperationException("The RPC completed successfully instead of observing cancellation.");
    }

    public void Dispose() => _cancellation?.Dispose();
}

internal static class StateComparison
{
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

// Closing the gate is an admission barrier, not a timer or an assumption about queue expiry.
internal sealed class InFlightCallGate
{
    public const string ClosedMessage = "Harness PushBroadcast gate is closed.";
    private readonly object _lock = new();
    private bool _blocked;
    private int _inFlight;
    private long _admitted;
    private long _rejected;
    private TaskCompletionSource? _drained;

    public TreeGateSnapshot Snapshot
    {
        get
        {
            lock (_lock)
            {
                return new(_blocked, _inFlight, _admitted, _rejected);
            }
        }
    }

    public async Task Invoke(Func<Task> action)
    {
        lock (_lock)
        {
            if (_blocked)
            {
                _rejected++;
                throw new InvalidOperationException(ClosedMessage);
            }

            _admitted++;
            _inFlight++;
        }

        try
        {
            await action();
        }
        finally
        {
            lock (_lock)
            {
                if (--_inFlight == 0)
                {
                    _drained?.TrySetResult();
                }
            }
        }
    }

    public async Task BlockAndDrain(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task pending;
        lock (_lock)
        {
            _blocked = true;
            pending = _inFlight == 0
                ? Task.CompletedTask
                : (_drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        await pending.WaitAsync(cancellationToken);
    }

    public void Open()
    {
        lock (_lock)
        {
            if (_inFlight != 0)
            {
                throw new InvalidOperationException("Cannot reopen the tree gate before draining admitted calls.");
            }

            _blocked = false;
            _drained = null;
        }
    }
}

internal sealed record ApplyEvidence(
    string Namespace,
    string Key,
    long FromVersion,
    long ToVersion,
    string Result,
    string? Peer);

internal sealed record NodeSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; }
    public required BinaryIdentity Identity { get; init; }
    public required string Address { get; init; }
    public bool Enabled { get; init; }
    public bool NamespaceEnabled { get; init; }
    public long MembershipVersion { get; init; }
    public required SortedDictionary<string, string> Membership { get; init; }
    public required SortedDictionary<string, string> Load { get; init; }
    public required SortedDictionary<string, long> LoadVersions { get; init; }
    public required string[] ActiveMembers { get; init; }
    public required string[] UnconfirmedPeers { get; init; }
    public required string[] OriginatorTargets { get; init; }
    public required string[] ForwardingTargets { get; init; }
    public string[] TopologyMembers { get; init; } = [];
    public int Fanout { get; init; }
    public required Dictionary<string, MetricValue> Metrics { get; init; }
    public required ApplyEvidence[] Applies { get; init; }
    public long TransportBytesWritten { get; init; }
    public long TransportBytesRead { get; init; }
    public double SocketBytesSent { get; init; }
    public double SocketBytesReceived { get; init; }
    public long SocketCounterSamples { get; init; }
    public double CpuMilliseconds { get; init; }
    public long AllocatedBytes { get; init; }
    public long ManagedHeapBytes { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PrivateBytes { get; init; }
    public int PendingControlCalls { get; init; }
    public int StartedControlCalls { get; init; }
    public int CancelledControlCalls { get; init; }
    public int ControlCancellationSignals { get; init; }
    public bool ControlTokenCanBeCanceled { get; init; }
    public bool ControlTokenCancelledOnEntry { get; init; }
    public DateTimeOffset? ControlStartedAtUtc { get; init; }
    public DateTimeOffset? ControlCancellationObservedAtUtc { get; init; }
    public ControlledCallSnapshot? OutboundControlCall { get; init; }
    public bool Partitioned { get; init; }
    public bool LegacyGossipSuppressed { get; init; }
    public bool MembershipReadsFrozen { get; init; }
    public int? RemoteProcessId { get; init; }
    public string? ProbeError { get; init; }
    public bool ProbeTimedOut { get; init; }
    public long? RepairFromVersion { get; init; }
    public long? HeartbeatTicks { get; init; }
    public TreeGateSnapshot? TreeGate { get; init; }
    public bool? TreeProbeRejected { get; init; }
    public StateComparisonProbe? ComparisonProbe { get; init; }
}
