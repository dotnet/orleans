using System.Distributed.DurableTasks;
using Microsoft.AspNetCore.Mvc;

namespace DurableWorkflows;

public static class WorkflowEndpoints
{
    private static readonly PollingOptions PollImmediately = new() { PollTimeout = TimeSpan.Zero };

    public static IEndpointRouteBuilder MapDurableWorkflowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", () => Results.Text(
            "Durable Orleans workflows sample. See README.md for basic, fan-out, approval, cancellation, saga, and recovery scenarios."));

        endpoints.MapPost("/workflows/basic/{workflowId}", RunBasicAsync);
        endpoints.MapPost("/workflows/fan-out/{workflowId}", RunFanOutAsync);
        endpoints.MapPost("/workflows/approval/{correlationId}", StartApprovalAsync);
        endpoints.MapPut("/workflows/approval/{correlationId}", SubmitApprovalAsync);
        endpoints.MapGet("/workflows/approval/{correlationId}/status", GetApprovalStatusAsync);
        endpoints.MapPost("/workflows/cancellation/{cancellationId}", StartCancellationAsync);
        endpoints.MapDelete("/workflows/cancellation/{cancellationId}", SubmitCancellationAsync);
        endpoints.MapGet("/workflows/cancellation/{cancellationId}/status", GetCancellationStatusAsync);
        endpoints.MapPost("/workflows/orders/{orderId}", RunOrderAsync);
        return endpoints;
    }

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
        var scheduled = await workflow.RunBasicAsync(input).ScheduleAsync(RootId("basic", workflowId), cancellationToken);
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
        var scheduled = await workflow.RunFanOutAsync(items).ScheduleAsync(RootId("fanout", workflowId), cancellationToken);
        return Results.Ok(await scheduled.WaitAsync(cancellationToken));
    }

    private static async Task<IResult> StartApprovalAsync(
        IClusterClient client,
        string correlationId,
        string subject,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(correlationId))
        {
            return InvalidId();
        }

        try
        {
            var scheduled = await ScheduleApprovalAsync(client, correlationId, subject, cancellationToken);
            return Accepted("approval", correlationId, scheduled.Id);
        }
        catch (InvalidOperationException)
        {
            return Conflict();
        }
    }

    private static async Task<IResult> SubmitApprovalAsync(
        IClusterClient client,
        string correlationId,
        ApprovalSubmission submission,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(correlationId))
        {
            return InvalidId();
        }

        try
        {
            var scheduled = await ScheduleApprovalAsync(client, correlationId, submission.Subject, cancellationToken);
            await client.GetGrain<IApprovalGrain>(correlationId)
                .SubmitDecisionAsync(new(correlationId, submission.Approved, submission.Reason));
            return Accepted("approval", correlationId, scheduled.Id);
        }
        catch (InvalidOperationException)
        {
            return Conflict();
        }
    }

    private static async Task<IResult> GetApprovalStatusAsync(
        IClusterClient client,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(correlationId))
        {
            return InvalidId();
        }

        var snapshot = await client.GetGrain<IApprovalGrain>(correlationId).GetSnapshotAsync();
        if (snapshot.Subject is null)
        {
            return Missing();
        }

        try
        {
            var scheduled = await client.GetGrain<IWorkflowGrain>(correlationId)
                .RunApprovalAsync(new(correlationId, snapshot.Subject))
                .ScheduleAsync(RootId("approval", correlationId), cancellationToken);
            return Results.Ok(await GetStatusAsync<ApprovalWorkflowResult>(scheduled, cancellationToken));
        }
        catch (InvalidOperationException)
        {
            return Missing();
        }
    }

    private static async Task<IResult> StartCancellationAsync(
        IClusterClient client,
        string cancellationId,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(cancellationId))
        {
            return InvalidId();
        }

        try
        {
            var scheduled = await ScheduleCancellationAsync(client, cancellationId, cancellationToken);
            return Accepted("cancellation", cancellationId, scheduled.Id);
        }
        catch (InvalidOperationException)
        {
            return Conflict();
        }
    }

    private static async Task<IResult> SubmitCancellationAsync(
        IClusterClient client,
        string cancellationId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(cancellationId))
        {
            return InvalidId();
        }

        try
        {
            var scheduled = await ScheduleCancellationAsync(client, cancellationId, cancellationToken);
            await client.GetGrain<ICancellationGrain>(cancellationId)
                .RequestCancellationAsync(new(cancellationId, reason));
            return Accepted("cancellation", cancellationId, scheduled.Id);
        }
        catch (InvalidOperationException)
        {
            return Conflict();
        }
    }

    private static async Task<IResult> GetCancellationStatusAsync(
        IClusterClient client,
        string cancellationId,
        CancellationToken cancellationToken)
    {
        if (!IsValidId(cancellationId))
        {
            return InvalidId();
        }

        var snapshot = await client.GetGrain<ICancellationGrain>(cancellationId).GetSnapshotAsync();
        if (!snapshot.Registered)
        {
            return Missing();
        }

        try
        {
            var scheduled = await client.GetGrain<IWorkflowGrain>(cancellationId)
                .RunCancellationAsync(cancellationId)
                .ScheduleAsync(RootId("cancellation", cancellationId), cancellationToken);
            return Results.Ok(await GetStatusAsync<CancellationWorkflowResult>(scheduled, cancellationToken));
        }
        catch (InvalidOperationException)
        {
            return Missing();
        }
    }

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
            .ScheduleAsync(RootId("order", orderId), cancellationToken);
        return Results.Ok(await scheduled.WaitAsync(cancellationToken));
    }

    private static async Task<ScheduledTask<ApprovalWorkflowResult>> ScheduleApprovalAsync(
        IClusterClient client,
        string correlationId,
        string subject,
        CancellationToken cancellationToken)
    {
        await client.GetGrain<IApprovalGrain>(correlationId).RegisterRequestAsync(subject);
        var scheduled = await client.GetGrain<IWorkflowGrain>(correlationId)
            .RunApprovalAsync(new(correlationId, subject))
            .ScheduleAsync(RootId("approval", correlationId), cancellationToken);
        return scheduled;
    }

    private static async Task<ScheduledTask<CancellationWorkflowResult>> ScheduleCancellationAsync(
        IClusterClient client,
        string cancellationId,
        CancellationToken cancellationToken)
    {
        await client.GetGrain<ICancellationGrain>(cancellationId).RegisterRequestAsync();
        var scheduled = await client.GetGrain<IWorkflowGrain>(cancellationId)
            .RunCancellationAsync(cancellationId)
            .ScheduleAsync(RootId("cancellation", cancellationId), cancellationToken);
        return scheduled;
    }

    private static async Task<WorkflowStatusResponse> GetStatusAsync<TResult>(
        ScheduledTask<TResult> scheduled,
        CancellationToken cancellationToken)
    {
        var response = await scheduled.GetResponseAsync(PollImmediately, cancellationToken);
        return response.ResponseKind switch
        {
            DurableTaskResponseKind.None or DurableTaskResponseKind.Pending =>
                new(scheduled.Id.ToString(), "pending", null, null),
            DurableTaskResponseKind.Subscribed =>
                new(scheduled.Id.ToString(), "running", null, null),
            DurableTaskResponseKind.CompletedSuccessfully =>
                new(scheduled.Id.ToString(), "succeeded", response.GetResult<TResult>(), null),
            DurableTaskResponseKind.Canceled =>
                new(scheduled.Id.ToString(), "canceled", null, "Workflow was canceled."),
            DurableTaskResponseKind.Failed =>
                new(scheduled.Id.ToString(), "failed", null, "Workflow execution failed."),
            _ => throw new InvalidOperationException("Unknown durable task response.")
        };
    }

    private static IResult Accepted(string kind, string id, TaskId taskId)
    {
        var location = StatusLocation(kind, id);
        return Results.Accepted(location, new WorkflowAcceptedResponse(taskId.ToString(), location));
    }

    private static IResult InvalidId() =>
        Results.BadRequest(new WorkflowApiError("Workflow ids must be non-empty task-id segments without '/' and cannot begin with '$'."));

    private static IResult Conflict() =>
        Results.Conflict(new WorkflowApiError("The workflow id is already associated with a different request."));

    private static IResult Missing() =>
        Results.NotFound(new WorkflowApiError("The workflow was not found or its retained result has expired."));

    private static bool IsValidId(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.Contains('/') && !value.StartsWith('$');

    private static string RootId(string kind, string id) => $"{kind}-{id}";
    private static string StatusLocation(string kind, string id) => $"/workflows/{kind}/{id}/status";
}

public sealed record ApprovalSubmission(string Subject, bool Approved, string Reason);
public sealed record WorkflowAcceptedResponse(string TaskId, string StatusUrl);
public sealed record WorkflowStatusResponse(string TaskId, string Status, object? Result, string? Error);
public sealed record WorkflowApiError(string Error);
