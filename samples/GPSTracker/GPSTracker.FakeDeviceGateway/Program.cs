using GPSTracker.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using IHost host = Host.CreateDefaultBuilder(args)
    .UseOrleansClient((ctx, clientBuilder) => clientBuilder.UseLocalhostClustering())
    .UseConsoleLifetime()
    .Build();

await host.StartAsync();

IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
IClusterClient client = host.Services.GetRequiredService<IClusterClient>();

await LoadDriver.DriveLoad(client, 25, lifetime.ApplicationStopping);
await host.StopAsync();
