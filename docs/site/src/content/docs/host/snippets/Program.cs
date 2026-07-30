using Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Client;

using IHost host = Host.CreateDefaultBuilder(args)
    .UseOrleansClient((_, client) =>
    {
        client.UseLocalhostClustering();
        client.Services.AddTransient<IClientConnectionRetryFilter, ClientConnectRetryFilter>();
    })
    .Build();

await host.StartAsync();

var client = host.Services.GetRequiredService<IClusterClient>();
var playerId = Guid.Empty;
var game = Guid.Empty;

// <siloexc>
IPlayerGrain player = client.GetGrain<IPlayerGrain>(playerId);

try
{
    await player.JoinGame(game);
}
catch (SiloUnavailableException)
{
    // Lost connection to the cluster...
}
// </siloexc>
