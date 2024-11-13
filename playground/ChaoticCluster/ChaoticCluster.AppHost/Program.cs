using Projects;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<ChaoticCluster_Silo>("silo");

builder.Build().Run();
