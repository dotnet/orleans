using Azure.Data.Tables;
using Abstractions;
using Microsoft.AspNetCore.Mvc;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
var storageConnectionString = builder.Configuration["StorageConnectionString"]
    ?? throw new InvalidOperationException("StorageConnectionString is not configured.");

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddWebAppApplicationInsights("Minimal API Client");
builder.Logging.SetMinimumLevel(LogLevel.Warning).AddJsonConsole();

// if debugging, wait for the back-end services to start before connecting
if(Debugger.IsAttached)
{
    Console.WriteLine("Waiting 5 seconds for the Orleans cluster to start.");
    await Task.Delay(5000);
}

builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder
        .Configure<ClusterOptions>(options =>
        {
            options.ClusterId = "Cluster";
            options.ServiceId = "Service";
        })
        .UseAzureStorageClustering(options => options.TableServiceClient = new TableServiceClient(storageConnectionString));
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// reference the grain factory for the cluster
var clusterClient = app.Services.GetRequiredService<IClusterClient>();

// -------------------
// map the API methods
// -------------------

// server is up
app.MapGet("/", () => "The silo is up and running.")
   .Produces<string>(StatusCodes.Status200OK)
   .WithName("Status");

// gets the list of active Orleans grains in the cluster
app.MapGet("/grains", async () =>
{
    var managementGrain = clusterClient.GetGrain<IManagementGrain>(0);
    var stats = await managementGrain.GetSimpleGrainStatistics();
    var hosts = await managementGrain.GetDetailedHosts(onlyActive: true);
    var result = stats.Select(x => new GrainSummary(x.GrainType, x.ActivationCount, hosts.First(y => y.SiloAddress == x.SiloAddress).SiloName));
    return Results.Ok(result);
}).Produces<GrainSummary[]>(StatusCodes.Status200OK)
  .WithName("Grains");

// gets the list of hello grains in the system
app.MapGet("/providers", async () =>
{
    var managementGrain = clusterClient.GetGrain<IManagementGrain>(0);
    var allGrains = await managementGrain.GetDetailedGrainStatistics();
    var grains = allGrains.Where(x => x.GrainType.Contains("Hello")).Select(x => x.GrainId.GetIntegerKey()).OrderBy(x => x).ToArray();
    return Results.Ok(grains);
}).Produces<long[]>(StatusCodes.Status200OK).WithName("GetHelloProviders");

// gets a hello message from a grain
app.MapGet("/hello/{grain:int}", async (int grain) => {
    if (grain is < 0 or > 255)
    {
        return Results.BadRequest(new ProblemDetails
        {
            Title = "Invalid grain key",
            Detail = "Grain keys must be integers between 0 and 255.",
            Status = StatusCodes.Status400BadRequest
        });
    }

    var helloGrain = clusterClient.GetGrain<IHelloGrain>(grain);
    return Results.Ok(new WelcomeMessage(await helloGrain.SayHello()));
}).Produces<WelcomeMessage>(StatusCodes.Status200OK)
  .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
  .WithName("Welcome");

app.Run();

// record to show a summary of the cluster
public record GrainSummary(string GrainType, int Count, string Host);

// record to show the message from the grain
public record WelcomeMessage(string GrainType);