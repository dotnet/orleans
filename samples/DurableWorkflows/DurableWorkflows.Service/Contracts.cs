using Orleans.DurableTasks;

namespace DurableWorkflows;

public interface IWorkflowGrain : IGrainWithStringKey
{
    DurableTask<BasicWorkflowResult> RunBasicAsync(string input);
    DurableTask<FanOutWorkflowResult> RunFanOutAsync(string[] items);
    DurableTask<ApprovalWorkflowResult> RunApprovalAsync(ApprovalRequest request);
    DurableTask<CancellationWorkflowResult> RunCancellationAsync(TimeSpan holdPeriod);
    DurableTask<OrderWorkflowResult> RunOrderSagaAsync(OrderRequest request);
    Task<WorkflowRuntimeInfo> GetRuntimeInfoAsync();
}

public interface IActivityGrain : IGrainWithStringKey
{
    DurableTask<ActivityResult> ExecuteAsync(ActivityRequest request);
    Task<ActivitySnapshot> GetSnapshotAsync();
}

public interface IInventoryGrain : IGrainWithStringKey
{
    DurableTask<SagaEffect> ReserveAsync(string operationId);
    DurableTask<SagaEffect> ReleaseAsync(string operationId);
    Task<SagaParticipantSnapshot> GetSnapshotAsync();
}

public interface IPaymentGrain : IGrainWithStringKey
{
    DurableTask<SagaEffect> ChargeAsync(string operationId);
    DurableTask<SagaEffect> RefundAsync(string operationId);
    Task<SagaParticipantSnapshot> GetSnapshotAsync();
}

public static class WorkflowSignals
{
    // An approval workflow waits for this signal, which reviewers send to its root task.
    public static readonly DurableSignal<ApprovalDecision> ApprovalDecided = new("approval-decided");
}

[GenerateSerializer, Immutable]
public sealed record ActivityRequest(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string Input,
    [property: Id(2)] bool Fail = false);

// TaskId identifies the durable task which recorded the effect. A task which a workflow invokes runs under an ID scoped to its caller, which ends with the workflow's stable child ID, such as "fanout-{id}/branches/0".
[GenerateSerializer, Immutable]
public sealed record ActivityResult(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string Output,
    [property: Id(2)] string TaskId);

[GenerateSerializer, Immutable]
public sealed record ActivitySnapshot(
    [property: Id(0)] IReadOnlyDictionary<string, ActivityResult> Effects);

[GenerateSerializer, Immutable]
public sealed record BasicWorkflowResult(
    [property: Id(0)] string WorkflowId,
    [property: Id(1)] string Output);

[GenerateSerializer, Immutable]
public sealed record FanOutWorkflowResult(
    [property: Id(0)] string WorkflowId,
    [property: Id(1)] ActivityResult[] Branches);

[GenerateSerializer, Immutable]
public sealed record ApprovalRequest(
    [property: Id(0)] string Subject,
    [property: Id(1)] TimeSpan Timeout);

[GenerateSerializer, Immutable]
public sealed record ApprovalDecision(
    [property: Id(0)] bool Approved,
    [property: Id(1)] string Reason);

[GenerateSerializer, Immutable]
public sealed record ApprovalWorkflowResult(
    [property: Id(0)] string WorkflowId,
    [property: Id(1)] string Subject,
    [property: Id(2)] bool Approved,
    [property: Id(3)] string Reason,
    [property: Id(4)] string SiloAddress,
    [property: Id(5)] Guid ActivationId);

[GenerateSerializer, Immutable]
public sealed record CancellationWorkflowResult(
    [property: Id(0)] string WorkflowId,
    [property: Id(1)] bool Canceled,
    [property: Id(2)] string? Reason);

[GenerateSerializer, Immutable]
public sealed record WorkflowRuntimeInfo(
    [property: Id(0)] string SiloAddress,
    [property: Id(1)] Guid ActivationId);

[GenerateSerializer, Immutable]
public sealed record OrderRequest(
    [property: Id(0)] string OrderId,
    [property: Id(1)] bool FailShipping);

[GenerateSerializer]
public enum OrderWorkflowStatus
{
    Completed,
    Compensated
}

[GenerateSerializer, Immutable]
public sealed record OrderWorkflowResult(
    [property: Id(0)] string OrderId,
    [property: Id(1)] OrderWorkflowStatus Status,
    [property: Id(2)] string[] Compensations,
    [property: Id(3)] string? Failure);

[GenerateSerializer, Immutable]
public sealed record SagaEffect(
    [property: Id(0)] string OperationId,
    [property: Id(1)] string Kind);

[GenerateSerializer, Immutable]
public sealed record SagaParticipantSnapshot(
    [property: Id(0)] IReadOnlyDictionary<string, SagaEffect> Effects);
