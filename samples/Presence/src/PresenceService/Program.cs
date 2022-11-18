using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Console.Title = "Presence Server";

await Host.CreateDefaultBuilder()
    .UseOrleans(builder =>
    {
        builder.UseLocalhostClustering();
    })
    .ConfigureLogging(builder => builder.AddConsole())
    .RunConsoleAsync();