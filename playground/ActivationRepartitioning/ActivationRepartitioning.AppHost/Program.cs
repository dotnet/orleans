using Aspire.Hosting;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("orleans-redis");

var orleans = builder.AddOrleans("cluster")
    .WithClustering(redis);

builder.AddProject<ActivationRepartitioning_Frontend>("frontend")
    .WithReference(orleans)
    .WaitFor(redis)
    .WithReplicas(5);

builder.Build().Run();
