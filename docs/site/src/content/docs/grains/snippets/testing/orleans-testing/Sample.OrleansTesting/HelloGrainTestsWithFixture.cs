namespace Tests;

// <shared_cluster_test>
[Collection(ClusterCollection.Name)]
public sealed class HelloGrainTestsWithFixture(ClusterFixture fixture)
{
    [Fact]
    public async Task SaysHelloCorrectly()
    {
        var hello = fixture.Cluster.Client.GetGrain<IHelloGrain>(Guid.NewGuid());
        var greeting = await hello.SayHello("World");

        Assert.Equal("Hello, World!", greeting);
    }
}
// </shared_cluster_test>
