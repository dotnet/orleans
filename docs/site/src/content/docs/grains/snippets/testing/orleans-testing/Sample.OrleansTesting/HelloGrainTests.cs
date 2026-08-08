using Orleans.TestingHost;

namespace Tests;

// <basic_cluster_test>
public sealed class HelloGrainTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new InProcessTestClusterBuilder();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    [Fact]
    public async Task SaysHelloCorrectly()
    {
        var hello = _cluster.Client.GetGrain<IHelloGrain>(Guid.NewGuid());
        var greeting = await hello.SayHello("World");

        Assert.Equal("Hello, World!", greeting);
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();
}
// </basic_cluster_test>
