var builder = DistributedApplication.CreateBuilder(args);
builder.AddAzureProvisioning();

var azureStorage = builder.AddAzureStorage("az-storage").RunAsEmulator(builder =>
    builder
        .WithImageTag("3.33.0"));
        //.WithLifetime(ContainerLifetime.Persistent));
var azureBlobs = azureStorage.AddBlobs("state");

var orleans = builder.AddOrleans("orleans")
    .WithClustering(azureStorage.AddTables("clustering"));

builder.AddProject<Projects.WorkflowsApp_Service>("workflowsapp-service")
    .WithReference(orleans)
    .WithReference(azureBlobs, "state")
    .WaitFor(azureStorage);

builder.Build().Run();
