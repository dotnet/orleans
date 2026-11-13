using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("clustering");
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(configure => configure.WithLifetime(ContainerLifetime.Persistent));
var blobs = storage.AddBlobs("durable-state");
var orleans = builder.AddOrleans("default")
    .WithClustering(redis);

builder.AddProject<Projects.DurableWorkflows_Service>("service")
    .WithReference(orleans)
    .WithReference(blobs)
    .WaitFor(redis)
    .WaitFor(blobs)
    .WithReplicas(2)
    .WithExternalHttpEndpoints();

builder.Build().Run();
