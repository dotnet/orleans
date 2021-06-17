using System;
using Chirper.Grains;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;

Console.Title = "Chirper Server";

await Host.CreateDefaultBuilder()
    .UseOrleans(builder =>
    {
        builder
            .UseLocalhostClustering()
            .AddMemoryGrainStorage("AccountState")
            .ConfigureApplicationParts(parts => parts
                .AddApplicationPart(typeof(ChirperAccount).Assembly)
                .AddApplicationPart(typeof(IChirperAccount).Assembly))
            .UseDashboard();
    })
    .ConfigureLogging(builder =>
    {
        builder
            .AddFilter("Orleans.Runtime.Management.ManagementGrain", LogLevel.Warning)
            .AddFilter("Orleans.Runtime.SiloControl", LogLevel.Warning);
    })
    .RunConsoleAsync();
