using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Presence.Grains;
using Presence.PlayerWatcher;
using Presence.Shared;

Console.Title = "PlayerWatcher";

await Host.CreateDefaultBuilder()
    .ConfigureServices(services =>
    {
        // add regular services
        services.AddTransient<IGameObserver, LoggerGameObserver>();

        // this hosted service connects and disconnects from the cluster along with the host
        // it also exposes the cluster client to other services that request it
        services.AddSingleton<ClusterClientHostedService>();
        services.AddSingleton<IHostedService>(_ => _.GetService<ClusterClientHostedService>());
        services.AddSingleton(_ => _.GetService<ClusterClientHostedService>().Client);

        // this hosted service runs the sample logic
        services.AddSingleton<IHostedService, PlayerWatcherHostedService>();
    })
    .RunConsoleAsync();
