using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestingHost;
using MSTestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace Tests;

// <mstest_cluster_fixture>
[TestClass]
public sealed class MSTestClusterFixture
{
    public static InProcessTestCluster Cluster { get; private set; } = null!;

    [AssemblyInitialize]
    public static async Task Initialize(Microsoft.VisualStudio.TestTools.UnitTesting.TestContext _)
    {
        var builder = new InProcessTestClusterBuilder();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    [AssemblyCleanup]
    public static async Task Cleanup() => await Cluster.DisposeAsync();
}
// </mstest_cluster_fixture>

// <mstest_shared_cluster_test>
[TestClass]
public sealed class HelloGrainMSTests
{
    [TestMethod]
    public async Task SharedClusterSaysHelloCorrectly()
    {
        var hello = MSTestClusterFixture.Cluster.Client.GetGrain<IHelloGrain>(Guid.NewGuid());
        var greeting = await hello.SayHello("World");

        MSTestAssert.AreEqual("Hello, World!", greeting);
    }
}
// </mstest_shared_cluster_test>
