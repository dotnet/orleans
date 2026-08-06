using Aspire.Hosting;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator();

var blobs = storage.AddBlobs("blobs");

builder.AddProject<JournalingAzureBlobJson>("journaling-json")
    .WithReference(blobs)
    .WaitFor(blobs)
    .WithEnvironment("ORLEANS_JOURNALING_CONTAINER", "orleans-journaling-json-sample")
    .WithEnvironment("ORLEANS_JOURNALING_BLOB", "journaled-json-sample.jsonl");

builder.Build().Run();
