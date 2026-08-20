using Azure.Storage.Blobs;
using DurableWorkflows;
using System.Distributed.DurableTasks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Journaling;

var builder = WebApplication.CreateBuilder(args);

builder.AddKeyedRedisClient("clustering");
builder.AddAzureBlobServiceClient("durable-state");
builder.Services.AddHealthChecks()
    .AddCheck("self", static () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), ["live"]);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .AddDurableTasks(options => options.ResultRetentionPeriod = TimeSpan.FromHours(24))
        .UseAzureBlobDurableJobs(options => options.ContainerName = "durable-workflows")
        .Configure<JournaledStateManagerOptions>(options => options.JournalFormatKey = "orleans-binary");
});

builder.Services.AddOptions<AzureBlobJournalStorageOptions>()
    .Configure<BlobServiceClient>((options, blobServiceClient) => options.BlobServiceClient = blobServiceClient);

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapHealthChecks("/alive", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});

app.MapGet("/", () => Results.Text(
    "Durable Orleans workflows sample. See README.md for basic, fan-out, approval, cancellation, saga, and recovery scenarios."));

app.MapPost("/workflows/basic/{workflowId}", async (
    IClusterClient client,
    string workflowId,
    string input,
    CancellationToken cancellationToken) =>
{
    var workflow = client.GetGrain<IWorkflowGrain>(ValidateId(workflowId));
    var scheduled = await workflow.RunBasicAsync(input).ScheduleAsync(RootId("basic", workflowId), cancellationToken);
    return Results.Ok(await scheduled.WaitAsync(cancellationToken));
});

app.MapPost("/workflows/fan-out/{workflowId}", async (
    IClusterClient client,
    string workflowId,
    [FromBody] string[] items,
    CancellationToken cancellationToken) =>
{
    var workflow = client.GetGrain<IWorkflowGrain>(ValidateId(workflowId));
    var scheduled = await workflow.RunFanOutAsync(items).ScheduleAsync(RootId("fanout", workflowId), cancellationToken);
    return Results.Ok(await scheduled.WaitAsync(cancellationToken));
});

app.MapPost("/workflows/approval/{correlationId}", async (
    IClusterClient client,
    string correlationId,
    string subject,
    CancellationToken cancellationToken) =>
{
    ValidateId(correlationId);
    var workflow = client.GetGrain<IWorkflowGrain>(correlationId);
    var scheduled = await workflow.RunApprovalAsync(new(correlationId, subject))
        .ScheduleAsync(RootId("approval", correlationId), cancellationToken);
    return Results.Accepted(
        $"/workflows/approval/{correlationId}",
        new { scheduled.Id, CorrelationId = correlationId, Subject = subject });
});

app.MapPut("/workflows/approval/{correlationId}", async (
    IClusterClient client,
    string correlationId,
    ApprovalSubmission submission,
    CancellationToken cancellationToken) =>
{
    ValidateId(correlationId);
    await client.GetGrain<IApprovalGrain>(correlationId)
        .SubmitDecisionAsync(new(correlationId, submission.Approved, submission.Reason));
    var workflow = client.GetGrain<IWorkflowGrain>(correlationId);
    var scheduled = await workflow.RunApprovalAsync(new(correlationId, submission.Subject))
        .ScheduleAsync(RootId("approval", correlationId), cancellationToken);
    return Results.Ok(await scheduled.WaitAsync(cancellationToken));
});

app.MapPost("/workflows/cancellation/{cancellationId}", async (
    IClusterClient client,
    string cancellationId,
    CancellationToken cancellationToken) =>
{
    var workflow = client.GetGrain<IWorkflowGrain>(ValidateId(cancellationId));
    var scheduled = await workflow.RunCancellationAsync(cancellationId)
        .ScheduleAsync(RootId("cancellation", cancellationId), cancellationToken);
    return Results.Accepted($"/workflows/cancellation/{cancellationId}", new { scheduled.Id });
});

app.MapDelete("/workflows/cancellation/{cancellationId}", async (
    IClusterClient client,
    string cancellationId,
    string reason,
    CancellationToken cancellationToken) =>
{
    ValidateId(cancellationId);
    await client.GetGrain<ICancellationGrain>(cancellationId)
        .RequestCancellationAsync(new(cancellationId, reason));
    var workflow = client.GetGrain<IWorkflowGrain>(cancellationId);
    var scheduled = await workflow.RunCancellationAsync(cancellationId)
        .ScheduleAsync(RootId("cancellation", cancellationId), cancellationToken);
    return Results.Ok(await scheduled.WaitAsync(cancellationToken));
});

app.MapPost("/workflows/orders/{orderId}", async (
    IClusterClient client,
    string orderId,
    bool failShipping,
    CancellationToken cancellationToken) =>
{
    var workflow = client.GetGrain<IWorkflowGrain>(ValidateId(orderId));
    var scheduled = await workflow.RunOrderSagaAsync(new(orderId, failShipping))
        .ScheduleAsync(RootId("order", orderId), cancellationToken);
    return Results.Ok(await scheduled.WaitAsync(cancellationToken));
});

await app.RunAsync();

static string ValidateId(string value)
{
    if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.StartsWith('$'))
    {
        throw new ArgumentException("Workflow ids must be non-empty task-id segments without '/' and cannot begin with '$'.", nameof(value));
    }

    return value;
}

static string RootId(string kind, string id) => $"{kind}-{ValidateId(id)}";

public sealed record ApprovalSubmission(string Subject, bool Approved, string Reason);
