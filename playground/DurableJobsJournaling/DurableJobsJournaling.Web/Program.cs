using System.Text.Json.Serialization;
using DurableJobsJournaling.Abstractions;
using DurableJobsJournaling.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Orleans.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddKeyedAzureTableServiceClient("tables");
builder.UseOrleansClient(clientBuilder => clientBuilder.AddActivityPropagation());
builder.Services.AddSingleton<WorkflowMetrics>();
builder.Services.AddSingleton<LoadDriver>();
builder.Services.AddHostedService(static services => services.GetRequiredService<LoadDriver>());
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();
app.MapDefaultEndpoints();

var defaultFiles = new DefaultFilesOptions();
defaultFiles.DefaultFileNames.Clear();
defaultFiles.DefaultFileNames.Add("index.html");

app.UseDefaultFiles(defaultFiles);
app.UseStaticFiles();

app.MapGet("/api/load", (LoadDriver driver) => driver.GetState());
app.MapPut("/api/load", ([FromBody] LoadSettings settings, LoadDriver driver) => Results.Ok(driver.UpdateSettings(settings)));
app.MapPost("/api/load/start", async (
    HttpRequest request,
    LoadDriver driver,
    IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions) =>
{
    if (request.HasJsonContentType() && request.ContentLength is not 0)
    {
        var settings = await request.ReadFromJsonAsync<LoadSettings>(jsonOptions.Value.SerializerOptions, request.HttpContext.RequestAborted);
        if (settings is not null)
        {
            driver.UpdateSettings(settings);
        }
    }

    driver.Enable();
    return Results.Ok(driver.GetState());
});
app.MapPost("/api/load/stop", (LoadDriver driver) =>
{
    driver.Disable();
    return Results.Ok(driver.GetState());
});
app.MapPost("/api/load/drain", async (LoadDriver driver, CancellationToken cancellationToken) =>
{
    await driver.DrainAsync(cancellationToken);
    return Results.Ok(driver.GetState());
});
app.MapPost("/api/load/reset", async (LoadDriver driver, WorkflowMetrics metrics, CancellationToken cancellationToken) =>
{
    await driver.ResetAsync(cancellationToken);
    metrics.Reset();
    return Results.Ok(driver.GetState());
});

app.MapGet("/api/metrics/snapshot", (WorkflowMetrics metrics, LoadDriver driver) => metrics.GetSnapshot(driver.GetState()));
app.MapGet("/api/workflows/recent", (WorkflowMetrics metrics) => metrics.GetRecentWorkflows());
app.MapGet("/api/workflows/{workflowId:guid}", async (Guid workflowId, IClusterClient client) =>
{
    var grain = client.GetGrain<IWorkflowCoordinatorGrain>(workflowId);
    return Results.Ok(await grain.GetSnapshotAsync());
});
app.MapPost("/api/workflows/start", async ([FromBody] LoadSettings settings, IClusterClient client) =>
{
    var workflowId = Guid.NewGuid();
    var normalized = settings.Normalize();
    var grain = client.GetGrain<IWorkflowCoordinatorGrain>(workflowId);
    await grain.StartAsync(new WorkflowStartRequest(workflowId, DateTimeOffset.UtcNow, normalized));
    return Results.Ok(new { workflowId });
});
app.MapPost("/api/chaos/silo/kill", async (IClusterClient client, WorkflowMetrics metrics) =>
{
    var result = await client.GetGrain<IChaosGrain>("controller").TryKillSiloAsync();
    metrics.RecordChaos(result);
    return Results.Ok(result);
});
app.MapGet("/api/chaos/last", async (IClusterClient client) => await client.GetGrain<IChaosGrain>("controller").GetLastResultAsync());

app.Run();
