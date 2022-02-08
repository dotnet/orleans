using Chirper.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;

Console.Title = "Chirper Client";

await new HostBuilder()
    .ConfigureServices(
        services => services
            .AddSingleton<ClusterClientHostedService>()
            .AddSingleton<IHostedService>(sp => sp.GetRequiredService<ClusterClientHostedService>())
            .AddSingleton(sp => sp.GetRequiredService<ClusterClientHostedService>().Client)
            .AddSingleton<IHostedService, ShellHostedService>()
            .Configure<ConsoleLifetimeOptions>(sp => sp.SuppressStatusMessages = true))
    .ConfigureLogging(builder => builder.AddDebug())
    .RunConsoleAsync();
