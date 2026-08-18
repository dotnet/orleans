using Orleans.TestingHost;

namespace Tests;

// <topology_change_test>
public sealed class TopologyTests : IAsyncLifetime
{
    private InProcessTestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        _cluster = ClusterConfiguration.Create(new SharedTestState());
        await _cluster.DeployAsync();
    }

    [Fact]
    public async Task GrainCallSucceedsAfterSiloJoinsAndLeaves()
    {
        await ClusterConfiguration.AddAndRemoveSiloAsync(_cluster);

        var hello = _cluster.Client.GetGrain<IHelloGrain>(Guid.NewGuid());
        var greeting = await hello.SayHello("World");

        Assert.Equal("Hello, World!", greeting);
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();
}
// </topology_change_test>
