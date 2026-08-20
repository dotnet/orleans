using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.TransportTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public abstract class TransportTestsBase
{
    private readonly BaseTestClusterFixture _fixture;

    protected TransportTestsBase(BaseTestClusterFixture fixture)
    {
        _fixture = fixture;
        Assert.True(fixture.HostedCluster.Silos.Count >= 2);
    }

    [Fact, TestCategory("BVT"), TestCategory("Transport")]
    public async Task SimplePing()
    {
        var grain = _fixture.Client.GetGrain<IGenericPingSelf<int>>(Guid.NewGuid());
        var value = await grain.Ping(10);
        Assert.Equal(10, value);
    }

    [Fact, TestCategory("BVT"), TestCategory("Transport")]
    public async Task ProxyPing()
    {
        var grain1 = _fixture.Client.GetGrain<IGenericPingSelf<int>>(Guid.NewGuid());
        for (var i = 0; i < 10; i++)
        {
            var grain2 = _fixture.Client.GetGrain<IGenericPingSelf<int>>(Guid.NewGuid());
            var value = await grain1.PingOther(grain2, i);
            Assert.Equal(i, value);
        }
    }
}