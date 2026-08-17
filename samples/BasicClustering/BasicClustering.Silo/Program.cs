using BasicClustering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddKeyedRedisClient("clustering");
builder.UseOrleans();
builder.Services.AddHostedService<ClusterMonitor>();

await builder.Build().RunAsync();
