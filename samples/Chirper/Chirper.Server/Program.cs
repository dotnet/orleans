using Microsoft.Extensions.Hosting;

Console.Title = "Chirper Server";

await Host.CreateDefaultBuilder(args)
    .UseOrleans(
        builder => builder
            .UseLocalhostClustering()
            .AddMemoryGrainStorage("AccountState")
            .UseDashboard())
    .RunConsoleAsync();
