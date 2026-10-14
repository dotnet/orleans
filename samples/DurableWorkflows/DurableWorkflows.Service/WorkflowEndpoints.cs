using Microsoft.AspNetCore.Mvc;
using Orleans.DurableTasks;

namespace DurableWorkflows;

public static class WorkflowEndpoints
{
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromDays(3);
    private static readonly TimeSpan HoldPeriod = TimeSpan.FromDays(1);
    private static readonly PollingOptions PollImmediately = new() { PollTimeout = TimeSpan.Zero };

    public static IEndpointRouteBuilder MapDurableWorkflowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", () => Results.Text(
            "Durable Orleans workflows sample. See README.md for basic, fan-out, approval, cancellation, saga, and recovery scenarios."));

        endpoints.MapPost("/workflows/basic/{workflowId}", RunBasicAsync);
        endpoints.MapPost("/workflows/fan-out/{workflowId}", RunFanOutAsync);
        endpoints.MapPost("/workflows/approval/{workflowId}", StartApprovalAsync);
        endpoints.MapPut("/workflows/approval/{workflowId}", SubmitApprovalAsync);
        endpoints.MapGet("/workflows/approval/{workflowId}/status", GetApprovalStatusAsync);
        endpoints.MapPost("/workflows/cancellation/{workflowId}", StartCancellationAsync);
        endpoints.MapDelete("/workflows/cancellation/{workflowId}", CancelAsync);
        endpoints.MapGet("/workflows/cancellation/{workflowId}/status", GetCancellationStatusAsync);
        endpoints.MapPost("/workflows/orders/{orderId}", RunOrderAsync);
        return endpoints;
    }

    // The client waits for the result of the workflow which it schedules under a root ID derived from the workflow ID.
    // Repeating the request reattaches to that task while the grain retains it.
    private static async Task<IResult> RunBasicAsync(
        IClusterClient client,
        string workflowId,
        string input,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(workflowId))
        {
            return InvalidId();
        }

        var workflow = client.GetGrain<IWorkflowGrain>(workflowId);
        var scheduled = await workflow.RunBasicAsync(input).WithId(RootId("basic", workflowId)).ScheduleAsync(cancellationToken);
        return Results.Ok(await scheduled.WaitAsync(cancellationToken));
    }

    private static async Task<IResult> RunFanOutAsync(
        IClusterClient client,
        string workflowId,
        [FromBody] string[] items,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(workflowId))
        {
            return InvalidId();
        }

        var workflow = client.GetGrain<IWorkflowGrain>(workflowId);
        var scheduled = await workflow.RunFanOutAsync(items).WithId(RootId("fanout", workflowId)).ScheduleAsync(cancellationToken);
        return Results.Ok(await scheduled.WaitAsync(cancellationToken));
    }

    private static async Task<IResult> StartApprovalAsync(
        IClusterClient client,
        string workflowId,
        string subject,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(workflowId))
        {
            return InvalidId();
        }

        if (string.IsNullOrWhiteSpace(subject))
        {
            return InvalidSubject();
        }

        try
        {
            var scheduled = await ScheduleApprovalAsync(client, workflowId, subject, cancellationToken);
            return Accepted("approval", workflowId, scheduled.Id);
        }
        catch (InvalidOperationException)
        {
            return Conflict();
        }
    }

    // Starts the approval when it is not running yet, then signals the decision to its root task. The task records the
    // first decision, so repeating it is idempotent and a different decision conflicts.
    private static async Task<IResult> SubmitApprovalAsync(
        IClusterClient client,
        string workflowId,
        ApprovalSubmission submission,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(workflowId))
        {
            return InvalidId();
        }

        if (string.IsNullOrWhiteSpace(submission.Subject))
        {
            return InvalidSubject();
        }

        ScheduledTask<ApprovalWorkflowResult> scheduled;
        try
        {
            scheduled = await ScheduleApprovalAsync(client, workflowId, submission.Subject, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return Conflict();
        }

        var result = await client.GetGrain<IWorkflowGrain>(workflowId).TrySignalDurableTaskAsync(
            RootId("approval", workflowId),
            WorkflowSignals.ApprovalDecided,
            new ApprovalDecision(submission.Approved, submission.Reason),
            cancellationToken);
        return result switch
        {
            DurableSignalResult.Accepted or DurableSignalResult.Duplicate => Accepted("approval", workflowId, scheduled.Id),
            DurableSignalResult.TaskNotFound => Missing(),
            _ => Conflict(),
        };
    }

    private static Task<IResult> GetApprovalStatusAsync(
        IClusterClient client,
        string workflowId,
        CancellationToken cancellationToken) =>
        GetStatusAsync<ApprovalWorkflowResult>(client, "approval", workflowId, cancellationToken);

    private static async Task<IResult> StartCancellationAsync(
        IClusterClient client,
        string workflowId,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(workflowId))
        {
            return InvalidId();
        }

        var scheduled = await ScheduleCancellationAsync(client, workflowId, cancellationToken);
        return Accepted("cancellation", workflowId, scheduled.Id);
    }

    // Requests durable cancellation of the workflow, starting it first when it is not running yet. The runtime records
    // the first request and its reason with the task, and the workflow decides its outcome.
    private static async Task<IResult> CancelAsync(
        IClusterClient client,
        string workflowId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(workflowId))
        {
            return InvalidId();
        }

        var scheduled = await ScheduleCancellationAsync(client, workflowId, cancellationToken);
        await scheduled.CancelAsync(reason, cancellationToken);
        return Accepted("cancellation", workflowId, scheduled.Id);
    }

    private static Task<IResult> GetCancellationStatusAsync(
        IClusterClient client,
        string workflowId,
        CancellationToken cancellationToken) =>
        GetStatusAsync<CancellationWorkflowResult>(client, "cancellation", workflowId, cancellationToken);

    private static async Task<IResult> RunOrderAsync(
        IClusterClient client,
        string orderId,
        bool failShipping,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(orderId))
        {
            return InvalidId();
        }

        var workflow = client.GetGrain<IWorkflowGrain>(orderId);
        var scheduled = await workflow.RunOrderSagaAsync(new(orderId, failShipping))
            .WithId(RootId("order", orderId)).ScheduleAsync(cancellationToken);
        return Results.Ok(await scheduled.WaitAsync(cancellationToken));
    }

    // Scheduling an existing root ID with the same request reattaches to that task; a different subject conflicts.
    private static Task<ScheduledTask<ApprovalWorkflowResult>> ScheduleApprovalAsync(
        IClusterClient client,
        string workflowId,
        string subject,
        CancellationToken cancellationToken) =>
        client.GetGrain<IWorkflowGrain>(workflowId)
            .RunApprovalAsync(new(subject, ApprovalTimeout))
            .WithId(RootId("approval", workflowId)).ScheduleAsync(cancellationToken);

    private static Task<ScheduledTask<CancellationWorkflowResult>> ScheduleCancellationAsync(
        IClusterClient client,
        string workflowId,
        CancellationToken cancellationToken) =>
        client.GetGrain<IWorkflowGrain>(workflowId)
            .RunCancellationAsync(HoldPeriod)
            .WithId(RootId("cancellation", workflowId)).ScheduleAsync(cancellationToken);

    // Polls the retained root task without scheduling it. The grain retains a client-scheduled task's result for
    // DurableTaskOptions.ResultRetentionPeriod after completion; a missing or expired task returns 404.
    private static async Task<IResult> GetStatusAsync<TResult>(
        IClusterClient client,
        string kind,
        string workflowId,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(workflowId))
        {
            return InvalidId();
        }

        var task = client.GetGrain<IWorkflowGrain>(workflowId).GetDurableTask<TResult>(RootId(kind, workflowId));
        DurableTaskResponse response;
        try
        {
            response = await task.GetResponseAsync(PollImmediately, cancellationToken);
        }
        catch (DurableTaskNotFoundException)
        {
            return Missing();
        }

        var taskId = task.Id.ToString();
        WorkflowStatusResponse status = response.Status switch
        {
            DurableTaskStatus.CompletedSuccessfully => new(taskId, "succeeded", response.GetResult<TResult>(), null),
            DurableTaskStatus.Canceled => new(taskId, "canceled", null, "Workflow was canceled."),
            DurableTaskStatus.Failed => new(taskId, "failed", null, "Workflow execution failed."),
            _ => new(taskId, "pending", null, null),
        };
        return Results.Ok(status);
    }

    private static IResult Accepted(string kind, string workflowId, TaskId taskId)
    {
        var location = $"/workflows/{kind}/{Uri.EscapeDataString(workflowId)}/status";
        return Results.Accepted(location, new WorkflowAcceptedResponse(taskId.ToString(), location));
    }

    private static IResult InvalidId() =>
        Results.BadRequest(new WorkflowApiError("Workflow IDs must be non-empty task-ID segments without '/' and cannot begin with '$'."));

    private static IResult InvalidSubject() =>
        Results.BadRequest(new WorkflowApiError("Approval subjects must contain a non-whitespace value."));

    private static IResult Conflict() =>
        Results.Conflict(new WorkflowApiError("The workflow is already associated with a different request or decision."));

    private static IResult Missing() =>
        Results.NotFound(new WorkflowApiError("The workflow was not found or its retained result has expired."));

    private static bool IsValidId(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Contains('/') && !value.StartsWith('$');

    private static string RootId(string kind, string workflowId) => $"{kind}-{workflowId}";
}

public sealed record ApprovalSubmission(string Subject, bool Approved, string Reason);
public sealed record WorkflowAcceptedResponse(string TaskId, string StatusUrl);
public sealed record WorkflowStatusResponse(string TaskId, string Status, object? Result, string? Error);
public sealed record WorkflowApiError(string Error);
