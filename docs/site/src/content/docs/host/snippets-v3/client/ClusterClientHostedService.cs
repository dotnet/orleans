using Microsoft.Extensions.Hosting;
using Orleans;

namespace ClientSnippets;

// <cluster_client_hosted_service>
public class ClusterClientHostedService : IHostedService
{
    private readonly IClusterClient _client;

    public ClusterClientHostedService(IClusterClient client)
    {
        _client = client;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // A retry filter could be provided here.
        await _client.Connect();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _client.Close();

        _client.Dispose();
    }
}
// </cluster_client_hosted_service>
