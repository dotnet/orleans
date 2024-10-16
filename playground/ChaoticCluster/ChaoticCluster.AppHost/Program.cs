using Projects;

var builder = DistributedApplication.CreateBuilder(args);

/*
// Comment this out once Aspire no longer requires a 'workload' to build.
builder.AddProject<ChaoticCluster_Silo>("silo");
*/

builder.Build().Run();
