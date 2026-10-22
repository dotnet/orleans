using Aspire.Hosting;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator.WithDataVolume());
var blobs = storage.AddBlobs("blobs");
var clustering = storage.AddTables("clustering");
var orleans = builder.AddOrleans("default").WithClustering(clustering);
var runId = Guid.NewGuid().ToString("N");

var prepare = builder.AddProject<Projects.DurableJobsMigration>("prepare")
    .WithReference(orleans)
    .WithReference(blobs)
    .WaitFor(blobs)
    .WaitFor(clustering)
    .WithEnvironment("Migration__RunId", runId)
    .WithEnvironment("Migration__Phase", "prepare")
    .WithEnvironment("Migration__DueDelaySeconds", builder.Configuration.GetValue("Migration:DueDelaySeconds", 30).ToString());

builder.AddProject<Projects.DurableJobsMigration>("drain")
    .WithReference(orleans)
    .WithReference(blobs)
    .WaitFor(blobs)
    .WaitFor(clustering)
    .WaitForCompletion(prepare)
    .WithEnvironment("Migration__RunId", runId)
    .WithEnvironment("Migration__Phase", "drain");

builder.Build().Run();
