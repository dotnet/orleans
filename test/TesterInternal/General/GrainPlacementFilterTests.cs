using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Filtering;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.General;

[TestCategory("Placement"), TestCategory("Filters")]
public class GrainPlacementFilterTests : TestClusterPerTest
{
    protected override void ConfigureTestCluster(TestClusterBuilder builder)
    {
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
    }

    private class SiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
        {
            hostBuilder.ConfigureServices(services =>
            {
                services.AddPlacementFilter<TestPlacementFilterStrategy, TestPlacementFilterDirector>(ServiceLifetime.Singleton);
            });
        }
    }

    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_GrainWithoutFilterCanBeCalled()
    {
        await this.HostedCluster.WaitForLivenessToStabilizeAsync();
        var managementGrain = this.Client.GetGrain<IManagementGrain>(0);
        var silos = await managementGrain.GetHosts(true);
        Assert.NotNull(silos);
    }

    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_FilterIsTriggered()
    {
        await HostedCluster.WaitForLivenessToStabilizeAsync();
        var triggered = false;
        var task = Task.Run(async () =>
        {
            triggered = await TestPlacementFilterDirector.Triggered.WaitAsync(TimeSpan.FromSeconds(1));
        });
        var localOnlyGrain = Client.GetGrain<ITestFilteredGrain>(0);
        await localOnlyGrain.Ping();
        await task;
        Assert.True(triggered);
    }
}

[TestPlacementFilter]
public class TestFilteredGrain : Grain, ITestFilteredGrain
{
    public Task Ping() => Task.CompletedTask;
}

public interface ITestFilteredGrain : IGrainWithIntegerKey
{
    Task Ping();
}

public class TestPlacementFilterAttribute() : PlacementFilterAttribute(new TestPlacementFilterStrategy());

public class TestPlacementFilterStrategy : PlacementFilterStrategy;

public class TestPlacementFilterDirector() : IPlacementFilterDirector
{
    public static SemaphoreSlim Triggered { get; } = new(0);

    public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
    {
        Triggered.Release(1);
        return silos;
    }
}
