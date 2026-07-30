var builder = DistributedApplication.CreateBuilder(args);

// Add the resources which you will use for Orleans clustering and
// grain state storage.
var sessionStorage = builder.AddAzureStorage("sessionStorage")
    .RunAsEmulator();
var persistentStorage = builder.AddAzureStorage("persistentStorage")
    .RunAsEmulator(config => config.WithLifetime(ContainerLifetime.Persistent));

var clusteringTable = sessionStorage.AddTables("clustering");
var grainStorage = persistentStorage.AddBlobs("grain-state");

// Add the Orleans resource to the Aspire DistributedApplication
// builder, then configure it with Azure Table Storage for clustering
// and Azure Blob Storage for grain storage.
var orleans = builder.AddOrleans("default")
                     .WithClustering(clusteringTable)
                     .WithGrainStorage("Default", grainStorage);

builder.AddProject<Projects.JournaledTodoList_WebApp>("webapp")
       .WithReference(orleans)
       .WithReplicas(3)
       .WithExternalHttpEndpoints()
       .WaitFor(clusteringTable)
       .WaitFor(grainStorage);

builder.Build().Run();
