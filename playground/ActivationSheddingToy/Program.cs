using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Configuration;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.UseOrleans(orleans =>
{
    orleans.UseLocalhostClustering();
    orleans.AddDistributedGrainDirectory();
});

builder.Services.Configure<GrainCollectionOptions>(options =>
{
    options.EnableActivationSheddingOnMemoryPressure = true;
    options.MemoryUsageLimitPercentage = 80;
    options.MemoryUsageTargetPercentage = 50;
});

builder.Services.AddHostedService<ActivationSheddingToyHostedService>();
await builder.Build().RunAsync();

