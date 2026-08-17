var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("clustering");
var orleans = builder.AddOrleans("default")
    .WithClustering(redis);

builder.AddProject<Projects.BasicClustering_Silo>("silo")
    .WithReference(orleans)
    .WaitFor(redis)
    .WithReplicas(2);

builder.Build().Run();
