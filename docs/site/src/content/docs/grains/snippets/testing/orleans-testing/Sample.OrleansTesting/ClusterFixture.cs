using Orleans.TestingHost;

// <cluster_fixture>
public sealed class ClusterFixture : IAsyncLifetime
{
    public InProcessTestCluster Cluster { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async ValueTask DisposeAsync() => await Cluster.DisposeAsync();
}
// </cluster_fixture>
