using Orleans.TestingHost;

namespace Tests;

[Collection(ClusterCollection.Name)]
public class HelloGrainTestsWithFixture(ClusterFixture fixture)
{
    private readonly TestCluster _cluster = fixture.Cluster;

    [Fact]
    public async Task SaysHelloCorrectly()
    {
        var hello = _cluster.GrainFactory.GetGrain<IHelloGrain>(Guid.NewGuid());
        var greeting = await hello.SayHello("World");

        Assert.Equal("Hello, World!", greeting);
    }
}
