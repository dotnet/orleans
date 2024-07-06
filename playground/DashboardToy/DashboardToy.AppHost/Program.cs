using Projects;

var builder = DistributedApplication.CreateBuilder(args);
var redis = builder.AddRedis("orleans-redis");

var orleans = builder.AddOrleans("cluster")
    .WithClustering(redis);

/*
// Comment this out once Aspire no longer requires a 'workload' to build.
builder.AddProject<DashboardToy_Frontend>("frontend")
    .WithReference(orleans)
    .WithReplicas(5);
*/

builder.Build().Run();
