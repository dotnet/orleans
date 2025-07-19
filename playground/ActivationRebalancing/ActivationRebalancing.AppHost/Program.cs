var builder = DistributedApplication.CreateBuilder(args);

var redis = builder.AddRedis("orleans-redis");

var orleans = builder.AddOrleans("cluster")
    .WithClustering(redis);

var backend = builder.AddProject<Projects.ActivationRebalancing_Cluster>("backend")
    .WithReference(orleans)
    .WaitFor(redis)
    .WithReplicas(5);

builder.AddProject<Projects.ActivationRebalancing_Frontend>("frontend")
    .WithReference(orleans.AsClient())
    .WaitFor(backend)
    .WithReplicas(1);

builder.Build().Run();
