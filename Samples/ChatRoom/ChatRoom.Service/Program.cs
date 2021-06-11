using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;

await Host.CreateDefaultBuilder(args)
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            .AddMemoryGrainStorage("PubSubStore")
            .AddSimpleMessageStreamProvider("chat", options =>
            {
                options.FireAndForgetDelivery = true;
            });
    })
    .RunConsoleAsync();
