using Microsoft.Extensions.DependencyInjection;
using Orleans.Placement;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.PlacementFilterTests;

[TestCategory("Placement"), TestCategory("Filters")]
public class GrainPlacementFilterTests(GrainPlacementFilterTests.Fixture fixture) : IClassFixture<GrainPlacementFilterTests.Fixture>
{
    public static Dictionary<string, List<string>> FilterScratchpad = [];
    private static Random random = new();

    public class Fixture : IAsyncLifetime
    {
        public InProcessTestCluster Cluster { get; private set; }
        public async Task DisposeAsync()
        {
            if (Cluster is { } cluster)
            {
                await cluster.DisposeAsync();
            }
        }

        public async Task InitializeAsync()
        {
            var builder = new InProcessTestClusterBuilder(3);
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.Services.AddPlacementFilter<TestPlacementFilterStrategy, TestPlacementFilterDirector>(ServiceLifetime.Singleton);
                siloBuilder.Services.AddPlacementFilter<OrderAPlacementFilterStrategy, OrderAPlacementFilterDirector>(ServiceLifetime.Singleton);
                siloBuilder.Services.AddPlacementFilter<OrderBPlacementFilterStrategy, OrderBPlacementFilterDirector>(ServiceLifetime.Singleton);
            });

            Cluster = builder.Build();
            await Cluster.DeployAsync();
            await Cluster.WaitForLivenessToStabilizeAsync();
        }
    }

    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_GrainWithoutFilterCanBeCalled()
    {
        var managementGrain = fixture.Cluster.Client.GetGrain<IManagementGrain>(0);
        var silos = await managementGrain.GetHosts(true);
        Assert.NotNull(silos);
    }

    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_FilterIsTriggered()
    {
        var triggered = false;
        var task = Task.Run(async () =>
        {
            triggered = await TestPlacementFilterDirector.Triggered.WaitAsync(TimeSpan.FromSeconds(1));
        });
        var localOnlyGrain = fixture.Cluster.Client.GetGrain<ITestFilteredGrain>(0);
        await localOnlyGrain.Ping();
        await task;
        Assert.True(triggered);
    }

    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_OrderAB12()
    {
        var primaryKey = random.Next();
        var testGrain = fixture.Cluster.Client.GetGrain<ITestAB12FilteredGrain>(primaryKey);
        await testGrain.Ping();
        var list = FilterScratchpad.GetValueOrAddNew(testGrain.GetGrainId().Type.ToString());
        Assert.Equal(2, list.Count);
        Assert.Equal("A", list[0]);
        Assert.Equal("B", list[1]);
    }

    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_OrderAB21()
    {
        var primaryKey = random.Next();
        var testGrain = fixture.Cluster.Client.GetGrain<ITestAB21FilteredGrain>(primaryKey);
        await testGrain.Ping();
        var list = FilterScratchpad.GetValueOrAddNew(testGrain.GetGrainId().Type.ToString());
        Assert.Equal(2, list.Count);
        Assert.Equal("B", list[0]);
        Assert.Equal("A", list[1]);
    }

    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_OrderBA12()
    {
        var primaryKey = random.Next();
        var testGrain = fixture.Cluster.Client.GetGrain<ITestBA12FilteredGrain>(primaryKey);
        await testGrain.Ping();
        var list = FilterScratchpad.GetValueOrAddNew(testGrain.GetGrainId().Type.ToString());
        Assert.Equal(2, list.Count);
        Assert.Equal("B", list[0]);
        Assert.Equal("A", list[1]);
    }

    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_OrderBA21()
    {
        var primaryKey = random.Next();
        var testGrain = fixture.Cluster.Client.GetGrain<ITestBA21FilteredGrain>(primaryKey);
        await testGrain.Ping();

        var list = FilterScratchpad.GetValueOrAddNew(testGrain.GetGrainId().Type.ToString());
        Assert.Equal(2, list.Count);
        Assert.Equal("A", list[0]);
        Assert.Equal("B", list[1]);
    }

    [Fact, TestCategory("Functional")]
    public async Task PlacementFilter_DuplicateOrder()
    {
        var primaryKey = random.Next();
        var testGrain = fixture.Cluster.Client.GetGrain<ITestDuplicateOrderFilteredGrain>(primaryKey);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await testGrain.Ping();
        }); 
    }
}

[TestPlacementFilter(order: 1)]
public class TestFilteredGrain : Grain, ITestFilteredGrain
{
    public Task Ping() => Task.CompletedTask;
}

public interface ITestFilteredGrain : IGrainWithIntegerKey
{
    Task Ping();
}

public class TestPlacementFilterAttribute(int order) : PlacementFilterAttribute(new TestPlacementFilterStrategy(order));

public class TestPlacementFilterStrategy(int order) : PlacementFilterStrategy(order)
{
    public TestPlacementFilterStrategy() : this(0)
    {
    }
}

public class TestPlacementFilterDirector() : IPlacementFilterDirector
{
    public static SemaphoreSlim Triggered { get; } = new(0);

    public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
    {
        Triggered.Release(1);
        return silos;
    }
}



public class OrderAPlacementFilterAttribute(int order) : PlacementFilterAttribute(new OrderAPlacementFilterStrategy(order));

public class OrderAPlacementFilterStrategy(int order) : PlacementFilterStrategy(order)
{
    public OrderAPlacementFilterStrategy() : this(0)
    {
    }
}

public class OrderAPlacementFilterDirector : IPlacementFilterDirector
{
    public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
    {
        var dict = GrainPlacementFilterTests.FilterScratchpad;
        var list = dict.GetValueOrAddNew(target.GrainIdentity.Type.ToString());
        list.Add("A");
        return silos;
    }
}


public class OrderBPlacementFilterAttribute(int order) : PlacementFilterAttribute(new OrderBPlacementFilterStrategy(order));

public class OrderBPlacementFilterStrategy(int order) : PlacementFilterStrategy(order)
{

    public OrderBPlacementFilterStrategy() : this(0)
    {
    }
}

public class OrderBPlacementFilterDirector() : IPlacementFilterDirector
{
    public IEnumerable<SiloAddress> Filter(PlacementFilterStrategy filterStrategy, PlacementTarget target, IEnumerable<SiloAddress> silos)
    {
        var dict = GrainPlacementFilterTests.FilterScratchpad;
        var list = dict.GetValueOrAddNew(target.GrainIdentity.Type.ToString());
        list.Add("B");
        return silos;
    }
}

[OrderAPlacementFilter(order: 1)]
[OrderBPlacementFilter(order: 2)]
public class TestAB12FilteredGrain : Grain, ITestAB12FilteredGrain
{
    public Task Ping() => Task.CompletedTask;
}

public interface ITestAB12FilteredGrain : IGrainWithIntegerKey
{
    Task Ping();
}

[OrderAPlacementFilter(order: 2)]
[OrderBPlacementFilter(order: 1)]
public class TestAB21FilteredGrain : Grain, ITestAB21FilteredGrain
{
    public Task Ping() => Task.CompletedTask;
}

public interface ITestAB21FilteredGrain : IGrainWithIntegerKey
{
    Task Ping();
}

[OrderBPlacementFilter(order: 1)]
[OrderAPlacementFilter(order: 2)]
public class TestBA12FilteredGrain : Grain, ITestBA12FilteredGrain
{
    public Task Ping() => Task.CompletedTask;
}

public interface ITestBA12FilteredGrain : IGrainWithIntegerKey
{
    Task Ping();
}


[OrderBPlacementFilter(order: 2)]
[OrderAPlacementFilter(order: 1)]
public class TestBA121FilteredGrain : Grain, ITestBA21FilteredGrain
{
    public Task Ping() => Task.CompletedTask;
}

public interface ITestBA21FilteredGrain : IGrainWithIntegerKey
{
    Task Ping();
}



[OrderBPlacementFilter(order: 2)]
[OrderAPlacementFilter(order: 2)]
public class TestDuplicateOrderFilteredGrain : Grain, ITestDuplicateOrderFilteredGrain
{
    public Task Ping() => Task.CompletedTask;
}

public interface ITestDuplicateOrderFilteredGrain : IGrainWithIntegerKey
{
    Task Ping();
}
