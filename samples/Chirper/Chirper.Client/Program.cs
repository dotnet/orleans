using Chirper.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Console.Title = "Chirper Client";

await new HostBuilder()
    .UseOrleansClient(builder =>
        builder.UseLocalhostClustering())
    .ConfigureServices(
        services => services
            .AddSingleton<IHostedService, ShellHostedService>()
            .Configure<ConsoleLifetimeOptions>(sp => sp.SuppressStatusMessages = true))
    .ConfigureLogging(builder => builder.AddDebug())
    .RunConsoleAsync();
