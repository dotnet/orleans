var builder = DistributedApplication.CreateBuilder(args);

var storage = builder.AddAzureStorage("orleans-storage")
    .RunAsEmulator();
var clustering = storage.AddTables("clustering");
var grainState = storage.AddBlobs("grain-state");

var orleans = builder.AddOrleans("orleans")
    .WithClustering(clustering)
    .WithGrainStorage("Default", grainState);

var silo = builder.AddProject<Projects.OrleansApp_Silo>("silo")
    .WithReference(orleans)
    .WaitFor(clustering)
    .WaitFor(grainState);

builder.AddProject<Projects.OrleansApp_Client>("client")
    .WithReference(orleans.AsClient())
    .WaitFor(clustering)
    .WaitFor(silo);

builder.Build().Run();
