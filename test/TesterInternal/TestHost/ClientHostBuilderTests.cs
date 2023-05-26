using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.TestHost;

/// <summary>
/// Testing behaviors for registering services etc for the client host builder (that mirrors the silo behaviors)
/// </summary>
public class ClientHostBuilderTests : OrleansTestingBase
{
    private readonly TestCluster _testCluster;

    public ClientHostBuilderTests()
    {
        var builder = new TestClusterBuilder(1);
        _testCluster = builder.Build();
    }



    [Fact]
    public async Task ClientHostBuilder_Services_AreConfigured()
    {
        await _testCluster.DeployAsync();


    }
}
