using System.Text.Json;

namespace Orleans.Dissemination.PerformanceHarness;

internal sealed record NodeConfiguration(
    string Name,
    string ClusterId,
    string MembershipDirectory,
    int SiloPort,
    int ParentProcessId,
    bool Enabled,
    int SiloProcessorCount = 0,
    int GCConserveMemory = 0);

internal sealed record Command(int Id, string Operation, string? Peer = null, OpenLoopPlan? OpenLoop = null);

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

internal static class StateComparison
{
    private static readonly JsonSerializerOptions Options = new() { IncludeFields = true };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

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
    public string? FanoutSource { get; init; }
    public bool AggregationTree { get; init; }
    public int AntiEntropyPeerCount { get; init; }
    public double AntiEntropyIntervalMilliseconds { get; init; }
    public required Dictionary<string, MetricValue> Metrics { get; init; }
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
    public int ProcessorCount { get; init; }
    public bool ServerGC { get; init; }
    public bool Partitioned { get; init; }
    public int? RemoteProcessId { get; init; }
    public string? ProbeError { get; init; }
    public OpenLoopProgress? OpenLoopProgress { get; init; }
    public OpenLoopReport? OpenLoopReport { get; init; }
}
