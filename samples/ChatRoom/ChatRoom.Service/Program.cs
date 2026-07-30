using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

await Host.CreateDefaultBuilder(args)
    .UseOrleans(siloBuilder => siloBuilder.UseLocalhostClustering()
            .AddMemoryGrainStorage("PubSubStore")
            .AddMemoryStreams("chat")
            .ConfigureLogging(logging => logging.AddConsole()))
    .RunConsoleAsync();
