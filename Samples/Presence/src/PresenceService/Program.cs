using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Presence.Grains;

Console.Title = "Presence Server";

await Host.CreateDefaultBuilder()
    .UseOrleans(builder =>
    {
        builder
            .UseLocalhostClustering()
            .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(GameGrain).Assembly).WithReferences());
    })
    .ConfigureLogging(builder => builder.AddConsole())
    .RunConsoleAsync();
