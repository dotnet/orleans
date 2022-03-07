using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Presence.Shared;
using Presence.LoadGenerator;

Console.Title = nameof(Presence.LoadGenerator);

await new HostBuilder()
    .ConfigureServices(services =>
    {
        // this hosted service connects and disconnects from the cluster along with the host
        // it also exposes the cluster client to other services that request it
        services.AddSingleton<ClusterClientHostedService>();
        services.AddSingleton<IHostedService>(_ => _.GetRequiredService<ClusterClientHostedService>());
        services.AddSingleton(_ => _.GetRequiredService<ClusterClientHostedService>().Client);

        // this hosted service run the load generation using the available cluster client
        services.AddSingleton<IHostedService, LoadGeneratorHostedService>();
    })
    .ConfigureLogging(builder => builder.AddConsole())
    .RunConsoleAsync();
