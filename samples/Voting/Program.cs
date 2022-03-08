using Orleans;
using Orleans.Hosting;
using VotingData;

await Host.CreateDefaultBuilder(args)
    .UseOrleans((ctx, builder) =>
    {
        if (ctx.HostingEnvironment.IsDevelopment())
        {
            builder.UseLocalhostClustering();
            builder.AddMemoryGrainStorage("votes");
        }
        else
        {
            // In Kubernetes, we use environment variables and the pod manifest
            builder.UseKubernetesHosting();

            // Use Redis for clustering & persistence
            var redisAddress = $"{Environment.GetEnvironmentVariable("REDIS")}:6379";
            builder.UseRedisClustering(options => options.ConnectionString = redisAddress);
            builder.AddRedisGrainStorage("votes", options => options.ConnectionString = redisAddress);
        }

        builder.UseDashboard(options =>
        {
            options.Port = 8888;
        })
        .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(VoteGrain).Assembly));
    })
    .ConfigureWebHostDefaults(webBuilder =>
    {
        webBuilder.UseStartup<Startup>();
    })
    .RunConsoleAsync();