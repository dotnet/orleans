using Orleans.Concurrency;

namespace DurableJobsJournaling.Abstractions;

public interface IWorkflowCoordinatorGrain : IGrainWithGuidKey
{
    Task StartAsync(WorkflowStartRequest request);

    [AlwaysInterleave]
    Task<WorkflowResult> AwaitCompletionAsync();

    [AlwaysInterleave]
    Task<WorkflowSnapshot> GetSnapshotAsync();

    Task CancelAsync();
}

public interface IChaosGrain : IGrainWithStringKey
{
    Task<ChaosResult> TryKillSiloAsync();

    [AlwaysInterleave]
    Task<ChaosResult> GetLastResultAsync();
}

[GenerateSerializer]
public enum WorkflowStage
{
    Accept = 0,
    ReserveInventory = 1,
    ChargePayment = 2,
    PackShipment = 3,
    Audit = 4,
    Complete = 5
}

[GenerateSerializer]
public enum WorkflowStatus
{
    NotStarted = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Canceled = 4
}

[GenerateSerializer]
public enum ChildTaskRole
{
    Inventory = 0,
    Payment = 1,
    Shipping = 2,
    Audit = 3
}

[GenerateSerializer]
public enum DueTimeMode
{
    Immediate = 0,
    Delayed = 1,
    Alternating = 2
}

[GenerateSerializer, Immutable]
public sealed record LoadSettings(
    [property: Id(0)] int TargetConcurrency,
    [property: Id(1)] double TargetStartsPerSecond,
    [property: Id(3)] int StageDelayMilliseconds,
    [property: Id(4)] int StageJitterMilliseconds,
    [property: Id(5)] double FailureRate,
    [property: Id(6)] DueTimeMode DueTimeMode,
    [property: Id(7)] int StageDueDelayMilliseconds)
{
    public static LoadSettings Default { get; } = new(
        TargetConcurrency: 16,
        TargetStartsPerSecond: 8,
        StageDelayMilliseconds: 0,
        StageJitterMilliseconds: 0,
        FailureRate: 0,
        DueTimeMode: DueTimeMode.Immediate,
        StageDueDelayMilliseconds: 0);

    public LoadSettings Normalize() => this with
    {
        TargetConcurrency = Math.Clamp(TargetConcurrency, 0, 10_000),
        TargetStartsPerSecond = double.IsFinite(TargetStartsPerSecond) ? Math.Clamp(TargetStartsPerSecond, 0, 100_000) : 0,
        StageDelayMilliseconds = 0,
        StageJitterMilliseconds = 0,
        FailureRate = 0,
        DueTimeMode = DueTimeMode.Immediate,
        StageDueDelayMilliseconds = 0
    };
}

[GenerateSerializer, Immutable]
public sealed record WorkflowStartRequest(
    [property: Id(0)] Guid WorkflowId,
    [property: Id(1)] DateTimeOffset CreatedAt,
    [property: Id(2)] LoadSettings Settings);

[GenerateSerializer, Immutable]
public sealed record ChildTaskResult(
    [property: Id(0)] Guid WorkflowId,
    [property: Id(1)] WorkflowStage Stage,
    [property: Id(2)] ChildTaskRole Role,
    [property: Id(3)] int Sequence,
    [property: Id(4)] int Attempt,
    [property: Id(5)] DateTimeOffset StartedAt,
    [property: Id(6)] DateTimeOffset CompletedAt,
    [property: Id(7)] double LatencyMilliseconds,
    [property: Id(8)] string WorkerKey);

[GenerateSerializer, Immutable]
public sealed record WorkflowState(
    [property: Id(0)] Guid WorkflowId,
    [property: Id(1)] WorkflowStatus Status,
    [property: Id(2)] WorkflowStage ActiveStage,
    [property: Id(3)] DateTimeOffset CreatedAt,
    [property: Id(4)] DateTimeOffset UpdatedAt,
    [property: Id(5)] DateTimeOffset? CompletedAt,
    [property: Id(6)] int RetryCount,
    [property: Id(7)] int FailureCount,
    [property: Id(8)] string? Error,
    [property: Id(9)] LoadSettings Settings);

[GenerateSerializer, Immutable]
public sealed record WorkflowStageRecord(
    [property: Id(0)] WorkflowStage Stage,
    [property: Id(1)] int Attempt,
    [property: Id(2)] string DurableJobId,
    [property: Id(3)] DateTimeOffset ScheduledAt,
    [property: Id(4)] DateTimeOffset HandlerStartedAt,
    [property: Id(5)] DateTimeOffset CompletedAt,
    [property: Id(6)] double ScheduleToStartMilliseconds,
    [property: Id(7)] double ExecutionMilliseconds,
    [property: Id(8)] double RescheduleGapMilliseconds,
    [property: Id(9)] int ChildCount,
    [property: Id(10)] bool Succeeded,
    [property: Id(11)] string? Error,
    [property: Id(12)] DateTimeOffset PreviousStageCompletedAt = default,
    [property: Id(13)] DateTimeOffset StateWriteStartedAt = default,
    [property: Id(14)] DateTimeOffset StateWriteCompletedAt = default,
    [property: Id(15)] DateTimeOffset ScheduleRequestedAt = default,
    [property: Id(16)] DateTimeOffset ScheduleCompletedAt = default);

[GenerateSerializer, Immutable]
public sealed record WorkflowResult(
    [property: Id(0)] Guid WorkflowId,
    [property: Id(1)] WorkflowStatus Status,
    [property: Id(2)] DateTimeOffset CreatedAt,
    [property: Id(3)] DateTimeOffset CompletedAt,
    [property: Id(4)] double TotalLatencyMilliseconds,
    [property: Id(5)] WorkflowStageRecord[] Stages,
    [property: Id(6)] ChildTaskResult[] ChildResults,
    [property: Id(7)] int RetryCount,
    [property: Id(8)] int FailureCount,
    [property: Id(9)] string? Error);

[GenerateSerializer, Immutable]
public sealed record WorkflowSnapshot(
    [property: Id(0)] WorkflowState? State,
    [property: Id(1)] WorkflowStageRecord[] Stages,
    [property: Id(2)] ChildTaskResult[] ChildResults,
    [property: Id(3)] bool IsCompletionSet);

[GenerateSerializer, Immutable]
public sealed record LoadDriverState(
    [property: Id(0)] bool Enabled,
    [property: Id(1)] LoadSettings Settings,
    [property: Id(2)] int InFlight,
    [property: Id(3)] long Started,
    [property: Id(4)] long Completed,
    [property: Id(5)] long Failed,
    [property: Id(6)] DateTimeOffset? StartedAt);

[GenerateSerializer, Immutable]
public sealed record RateSnapshot(
    [property: Id(0)] double StartsPerSecond,
    [property: Id(1)] double CompletionsPerSecond,
    [property: Id(2)] double FailuresPerSecond,
    [property: Id(3)] double RetriesPerSecond);

[GenerateSerializer, Immutable]
public sealed record LatencySummary(
    [property: Id(0)] long Count,
    [property: Id(1)] double AverageMilliseconds,
    [property: Id(2)] double P50Milliseconds,
    [property: Id(3)] double P95Milliseconds,
    [property: Id(4)] double P99Milliseconds,
    [property: Id(5)] double MaxMilliseconds);

[GenerateSerializer, Immutable]
public sealed record WorkflowStageMetric(
    [property: Id(0)] WorkflowStage Stage,
    [property: Id(1)] long Completed,
    [property: Id(2)] long Failed,
    [property: Id(3)] double AverageLatencyMilliseconds,
    [property: Id(4)] LatencySummary HandoffLatency,
    [property: Id(5)] LatencySummary StateWriteLatency,
    [property: Id(6)] LatencySummary ScheduleCallLatency,
    [property: Id(7)] LatencySummary DueWaitLatency,
    [property: Id(8)] LatencySummary QueueLatency,
    [property: Id(9)] LatencySummary ExecutionLatency,
    [property: Id(10)] LatencySummary StageTotalLatency,
    [property: Id(11)] LatencySummary WorkflowElapsedLatency,
    [property: Id(12)] LatencySummary UnaccountedLatency);

[GenerateSerializer, Immutable]
public sealed record RecentWorkflowSummary(
    [property: Id(0)] Guid WorkflowId,
    [property: Id(1)] WorkflowStatus Status,
    [property: Id(2)] DateTimeOffset CompletedAt,
    [property: Id(3)] double TotalLatencyMilliseconds,
    [property: Id(4)] int RetryCount,
    [property: Id(5)] int FailureCount,
    [property: Id(6)] string? Error);

[GenerateSerializer, Immutable]
public sealed record MetricSnapshot(
    [property: Id(0)] DateTimeOffset Timestamp,
    [property: Id(1)] LoadDriverState Load,
    [property: Id(2)] RateSnapshot Rates,
    [property: Id(3)] LatencySummary EndToEndLatency,
    [property: Id(4)] LatencySummary ScheduleToStartLatency,
    [property: Id(5)] LatencySummary StageExecutionLatency,
    [property: Id(6)] LatencySummary RescheduleGapLatency,
    [property: Id(7)] WorkflowStageMetric[] Stages,
    [property: Id(8)] RecentWorkflowSummary[] Recent,
    [property: Id(9)] ChaosResult? LastChaos);

[GenerateSerializer, Immutable]
public sealed record ChaosResult(
    [property: Id(0)] bool Requested,
    [property: Id(1)] bool Succeeded,
    [property: Id(2)] string Message,
    [property: Id(3)] string? SiloAddress,
    [property: Id(4)] DateTimeOffset Timestamp,
    [property: Id(5)] string[] ActiveSilos);
