using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Presence.LoadGenerator;

Console.Title = nameof(Presence.LoadGenerator);

await new HostBuilder()
    .UseOrleansClient(builder =>
    {
        builder.UseLocalhostClustering();
    })
    .ConfigureServices(services =>
    {
        // This hosted service run the load generation using the available cluster client
        services.AddSingleton<IHostedService, LoadGeneratorHostedService>();
    })
    .ConfigureLogging(builder => builder.AddConsole())
    .RunConsoleAsync();
