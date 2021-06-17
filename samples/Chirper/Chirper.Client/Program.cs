using System;
using Chirper.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Spectre.Console;

Console.Title = "Chirper Client";

await new HostBuilder()
    .ConfigureServices(services =>
    {
        services
            .AddSingleton<ClusterClientHostedService>()
            .AddSingleton<IHostedService>(_ => _.GetService<ClusterClientHostedService>())
            .AddSingleton(_ => _.GetService<ClusterClientHostedService>().Client)
            .AddSingleton<IHostedService, ShellHostedService>()
            .Configure<ConsoleLifetimeOptions>(_ =>
            {
                _.SuppressStatusMessages = true;
            });
    })
    .ConfigureLogging(builder => builder.AddDebug())
    .RunConsoleAsync();
