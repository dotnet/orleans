using Orleans.TestingHost;

namespace Tests;

public class HelloGrainTests
{
    [Fact]
    public async Task SaysHelloCorrectly()
    {
        var builder = new TestClusterBuilder();
        var cluster = builder.Build();
        cluster.Deploy();

        var hello = cluster.GrainFactory.GetGrain<IHelloGrain>(Guid.NewGuid());
        var greeting = await hello.SayHello("World");

        cluster.StopAllSilos();

        Assert.Equal("Hello, World!", greeting);
    }
}
