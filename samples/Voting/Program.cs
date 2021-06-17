using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;
using Microsoft.AspNetCore.Builder;
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

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddControllersWithViews();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseStaticFiles();
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapDefaultControllerRoute();
        });
    }
}
