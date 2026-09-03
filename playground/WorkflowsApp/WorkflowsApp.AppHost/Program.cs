var builder = DistributedApplication.CreateBuilder(args);
builder.AddAzureProvisioning();

var azureStorage = builder.AddAzureStorage("az-storage").RunAsEmulator(builder =>
    builder
        .WithImageTag("3.33.0")
        .WithLifetime(ContainerLifetime.Session));
var azureBlobs = azureStorage.AddBlobs("state");

builder.AddProject<Projects.WorkflowsApp_Service>("workflowsapp-service")
    .WithArgs("--framework", "net10.0")
    .WithReference(azureBlobs, "state")
    .WaitFor(azureStorage);

builder.Build().Run();
