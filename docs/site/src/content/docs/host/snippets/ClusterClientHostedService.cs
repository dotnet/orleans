using Microsoft.Extensions.Hosting;

namespace Client;

public sealed class ClusterClientHostedService : IHostedService
{
    private readonly IClusterClient _client;

    public ClusterClientHostedService(IClusterClient client)
    {
        _client = client;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Use the _client to consume grains...

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
