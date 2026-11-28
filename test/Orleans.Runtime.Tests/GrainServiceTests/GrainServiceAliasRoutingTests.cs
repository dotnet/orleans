using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Services;
using Orleans.Services;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester;

public interface IProbeRouteBase : IGrainService
{
    Task<string> RouteId();
    Task<int> Value();
    Task<int> Add(int value);
}

public interface IProbeRouteMid : IProbeRouteBase
{
    Task<string> Mid();
}

public interface IProbeRouteLeaf : IProbeRouteMid
{
    Task<string> Leaf();
    Task<int> InitCount();
    Task<int> StartCount();
}

public interface IProbeRouteInitCaller : IGrainService
{
    Task<string> Seen();
}

public interface IProbeRouteSiblingA : IGrainService
{
    Task<string> RouteId();
    Task<int> Value();
    Task<int> Add(int value);
}

public interface IProbeRouteSiblingB : IGrainService
{
    Task<string> RouteId();
    Task<int> Value();
    Task<int> Add(int value);
}

public interface IProbeOwnedBase : IGrainService
{
    Task<string> Owner();
}

public interface IProbeOwnedLeaf : IProbeOwnedBase
{
}

public interface IProbeInitOrderEarly : IGrainService
{
    Task<bool> SawLaterRejectedBeforeInit();
}

public interface IProbeInitOrderLate : IGrainService
{
    Task<string> Ping();
}

public sealed class ProbeRouteBaseClient(IServiceProvider serviceProvider) : GrainServiceClient<IProbeRouteBase>(serviceProvider)
{
    public Task<string> RouteId(SiloAddress silo) => GetGrainService(silo).RouteId();
    public Task<int> Value(SiloAddress silo) => GetGrainService(silo).Value();
    public Task<int> Add(SiloAddress silo, int value) => GetGrainService(silo).Add(value);
}

public sealed class ProbeRouteMidClient(IServiceProvider serviceProvider) : GrainServiceClient<IProbeRouteMid>(serviceProvider)
{
    public Task<string> RouteId(SiloAddress silo) => GetGrainService(silo).RouteId();
    public Task<string> Mid(SiloAddress silo) => GetGrainService(silo).Mid();
    public Task<int> Value(SiloAddress silo) => GetGrainService(silo).Value();
    public Task<int> Add(SiloAddress silo, int value) => GetGrainService(silo).Add(value);
}

public sealed class ProbeRouteLeafClient(IServiceProvider serviceProvider) : GrainServiceClient<IProbeRouteLeaf>(serviceProvider)
{
    public Task<string> RouteId(SiloAddress silo) => GetGrainService(silo).RouteId();
    public Task<string> Leaf(SiloAddress silo) => GetGrainService(silo).Leaf();
    public Task<int> Value(SiloAddress silo) => GetGrainService(silo).Value();
    public Task<int> Add(SiloAddress silo, int value) => GetGrainService(silo).Add(value);
    public Task<int> InitCount(SiloAddress silo) => GetGrainService(silo).InitCount();
    public Task<int> StartCount(SiloAddress silo) => GetGrainService(silo).StartCount();
}

public sealed class ProbeRouteInitCallerClient(IServiceProvider serviceProvider) : GrainServiceClient<IProbeRouteInitCaller>(serviceProvider)
{
    public Task<string> Seen(SiloAddress silo) => GetGrainService(silo).Seen();
}

public sealed class ProbeRouteSiblingAClient(IServiceProvider serviceProvider) : GrainServiceClient<IProbeRouteSiblingA>(serviceProvider)
{
    public Task<string> RouteId(SiloAddress silo) => GetGrainService(silo).RouteId();
    public Task<int> Value(SiloAddress silo) => GetGrainService(silo).Value();
    public Task<int> Add(SiloAddress silo, int value) => GetGrainService(silo).Add(value);
}

public sealed class ProbeRouteSiblingBClient(IServiceProvider serviceProvider) : GrainServiceClient<IProbeRouteSiblingB>(serviceProvider)
{
    public Task<string> RouteId(SiloAddress silo) => GetGrainService(silo).RouteId();
    public Task<int> Value(SiloAddress silo) => GetGrainService(silo).Value();
    public Task<int> Add(SiloAddress silo, int value) => GetGrainService(silo).Add(value);
}

public sealed class ProbeOwnedBaseClient(IServiceProvider serviceProvider) : GrainServiceClient<IProbeOwnedBase>(serviceProvider)
{
    public Task<string> Owner(SiloAddress silo) => GetGrainService(silo).Owner();
}

public sealed class ProbeOwnedLeafClient(IServiceProvider serviceProvider) : GrainServiceClient<IProbeOwnedLeaf>(serviceProvider)
{
    public Task<string> Owner(SiloAddress silo) => GetGrainService(silo).Owner();
}

public sealed class ProbeInitOrderEarlyClient(IServiceProvider serviceProvider) : GrainServiceClient<IProbeInitOrderEarly>(serviceProvider)
{
    public Task<bool> SawLaterRejectedBeforeInit(SiloAddress silo) => GetGrainService(silo).SawLaterRejectedBeforeInit();
}

public sealed class ProbeInitOrderLateClient(IServiceProvider serviceProvider) : GrainServiceClient<IProbeInitOrderLate>(serviceProvider)
{
    public Task<string> Ping(SiloAddress silo) => GetGrainService(silo).Ping();
}

public sealed class ProbeRouteLeafService : GrainService, IProbeRouteLeaf
{
    private int _value;
    private int _initCount;
    private int _startCount;

    public ProbeRouteLeafService(GrainId id, Silo silo, ILoggerFactory loggerFactory) : base(id, silo, loggerFactory)
    {
    }

    public override Task Init(IServiceProvider serviceProvider)
    {
        _initCount++;
        return base.Init(serviceProvider);
    }

    public override Task Start()
    {
        _startCount++;
        return base.Start();
    }

    public Task<string> RouteId() => Task.FromResult("leaf");

    public Task<int> Value() => Task.FromResult(_value);

    public Task<int> Add(int value)
    {
        _value += value;
        return Task.FromResult(_value);
    }

    public Task<string> Mid() => Task.FromResult("mid");

    public Task<string> Leaf() => Task.FromResult("leaf-method");

    public Task<int> InitCount() => Task.FromResult(_initCount);

    public Task<int> StartCount() => Task.FromResult(_startCount);
}

public sealed class ProbeRouteInitCallerService : GrainService, IProbeRouteInitCaller
{
    private string _seen = "";

    public ProbeRouteInitCallerService(GrainId id, Silo silo, ILoggerFactory loggerFactory) : base(id, silo, loggerFactory)
    {
    }

    public override async Task Init(IServiceProvider serviceProvider)
    {
        await base.Init(serviceProvider);
        var client = serviceProvider.GetRequiredService<ProbeRouteBaseClient>();
        _seen = await client.RouteId(this.Silo);
    }

    public Task<string> Seen() => Task.FromResult(_seen);
}

public sealed class ProbeRouteSiblingService : GrainService, IProbeRouteSiblingA, IProbeRouteSiblingB
{
    private int _value;

    public ProbeRouteSiblingService(GrainId id, Silo silo, ILoggerFactory loggerFactory) : base(id, silo, loggerFactory)
    {
    }

    public Task<string> RouteId() => Task.FromResult("sibling");

    public Task<int> Value() => Task.FromResult(_value);

    public Task<int> Add(int value)
    {
        _value += value;
        return Task.FromResult(_value);
    }
}

public sealed class ProbeOwnedBaseService : GrainService, IProbeOwnedBase
{
    public ProbeOwnedBaseService(GrainId id, Silo silo, ILoggerFactory loggerFactory) : base(id, silo, loggerFactory)
    {
    }

    public Task<string> Owner() => Task.FromResult("base");
}

public sealed class ProbeOwnedLeafService : GrainService, IProbeOwnedLeaf
{
    public ProbeOwnedLeafService(GrainId id, Silo silo, ILoggerFactory loggerFactory) : base(id, silo, loggerFactory)
    {
    }

    public Task<string> Owner() => Task.FromResult("leaf");
}

public sealed class ProbeInitOrderEarlyService : GrainService, IProbeInitOrderEarly
{
    private bool _sawLaterRejectedBeforeInit;

    public ProbeInitOrderEarlyService(GrainId id, Silo silo, ILoggerFactory loggerFactory) : base(id, silo, loggerFactory)
    {
    }

    public override async Task Init(IServiceProvider serviceProvider)
    {
        await base.Init(serviceProvider);
        var late = serviceProvider.GetRequiredService<ProbeInitOrderLateClient>();
        try
        {
            await late.Ping(this.Silo);
        }
        catch (Exception exception) when (exception.Message.Contains("not active on this silo", StringComparison.Ordinal)
            || exception.ToString().Contains("not active on this silo", StringComparison.Ordinal))
        {
            _sawLaterRejectedBeforeInit = true;
        }
    }

    public Task<bool> SawLaterRejectedBeforeInit() => Task.FromResult(_sawLaterRejectedBeforeInit);
}

public sealed class ProbeInitOrderLateService : GrainService, IProbeInitOrderLate
{
    public ProbeInitOrderLateService(GrainId id, Silo silo, ILoggerFactory loggerFactory) : base(id, silo, loggerFactory)
    {
    }

    public Task<string> Ping() => Task.FromResult("late");
}

/// <summary>
/// Routes real grain-service calls through the silo registration path.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class GrainServiceAliasRoutingTests : OrleansTestingBase, IClassFixture<GrainServiceAliasRoutingTests.Fixture>
{
    private readonly Fixture _fixture;

    public GrainServiceAliasRoutingTests(Fixture fixture) => _fixture = fixture;

    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 2;
            builder.AddSiloBuilderConfigurator<AliasRoutingConfigurator>();
        }

        private sealed class AliasRoutingConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .AddGrainService<ProbeInitOrderEarlyService>()
                    .AddGrainService<ProbeInitOrderLateService>()
                    .AddGrainService<ProbeRouteLeafService>()
                    .AddGrainService<ProbeRouteInitCallerService>()
                    .AddGrainService<ProbeRouteSiblingService>()
                    .AddGrainService<ProbeOwnedBaseService>()
                    .AddGrainService<ProbeOwnedLeafService>()
                    .ConfigureServices(services => services
                        .AddSingleton<ProbeInitOrderEarlyClient>()
                        .AddSingleton<ProbeInitOrderLateClient>()
                        .AddSingleton<ProbeRouteBaseClient>()
                        .AddSingleton<ProbeRouteMidClient>()
                        .AddSingleton<ProbeRouteLeafClient>()
                        .AddSingleton<ProbeRouteInitCallerClient>()
                        .AddSingleton<ProbeRouteSiblingAClient>()
                        .AddSingleton<ProbeRouteSiblingBClient>()
                        .AddSingleton<ProbeOwnedBaseClient>()
                        .AddSingleton<ProbeOwnedLeafClient>());
            }
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
    public async Task HierarchyClients_ShareOneService()
    {
        var silo = Silos()[0];
        var address = silo.SiloAddress;
        var baseClient = silo.ServiceProvider.GetRequiredService<ProbeRouteBaseClient>();
        var midClient = silo.ServiceProvider.GetRequiredService<ProbeRouteMidClient>();
        var leafClient = silo.ServiceProvider.GetRequiredService<ProbeRouteLeafClient>();

        Assert.Equal("leaf", await baseClient.RouteId(address));
        Assert.Equal("leaf", await midClient.RouteId(address));
        Assert.Equal("mid", await midClient.Mid(address));
        Assert.Equal("leaf-method", await leafClient.Leaf(address));
        Assert.Equal(1, await leafClient.InitCount(address));
        Assert.Equal(1, await leafClient.StartCount(address));

        var before = await baseClient.Value(address);
        var after = await leafClient.Add(address, 1);
        Assert.Equal(before + 1, after);
        Assert.Equal(after, await midClient.Value(address));
    }

    [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
    public async Task Init_CallsEarlierServiceThroughItsBaseAlias()
    {
        foreach (var silo in Silos())
        {
            var seen = await silo.ServiceProvider.GetRequiredService<ProbeRouteInitCallerClient>().Seen(silo.SiloAddress);
            Assert.Equal("leaf", seen);
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
    public async Task Init_LaterServiceIsNotCallableBeforeItIsPublished()
    {
        foreach (var silo in Silos())
        {
            var early = silo.ServiceProvider.GetRequiredService<ProbeInitOrderEarlyClient>();
            var late = silo.ServiceProvider.GetRequiredService<ProbeInitOrderLateClient>();
            Assert.True(await early.SawLaterRejectedBeforeInit(silo.SiloAddress));
            Assert.Equal("late", await late.Ping(silo.SiloAddress));
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
    public async Task UnrelatedInterfaceClients_ShareOneService()
    {
        var silo = Silos()[0];
        var address = silo.SiloAddress;
        var first = silo.ServiceProvider.GetRequiredService<ProbeRouteSiblingAClient>();
        var second = silo.ServiceProvider.GetRequiredService<ProbeRouteSiblingBClient>();

        Assert.Equal("sibling", await first.RouteId(address));
        Assert.Equal(await first.RouteId(address), await second.RouteId(address));

        var before = await first.Value(address);
        var after = await second.Add(address, 1);
        Assert.Equal(before + 1, after);
        Assert.Equal(after, await first.Value(address));
    }

    [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
    public async Task SharedBase_RoutesToItsOwnPrimary()
    {
        var address = Silos()[0].SiloAddress;
        var provider = Silos()[0].ServiceProvider;
        Assert.Equal("base", await provider.GetRequiredService<ProbeOwnedBaseClient>().Owner(address));
        Assert.Equal("leaf", await provider.GetRequiredService<ProbeOwnedLeafClient>().Owner(address));
    }

    [Fact, TestCategory("BVT"), TestCategory("GrainServices")]
    public async Task CrossSilo_UpdatesOnlyTheDestinationService()
    {
        var silos = Silos();
        // The base address is an alias of the leaf service. Calling the leaf id would stay green if alias registration were removed.
        var caller = silos[0].ServiceProvider.GetRequiredService<ProbeRouteBaseClient>();
        var local = silos[0].SiloAddress;
        var remote = silos[1].SiloAddress;

        var localBefore = await caller.Value(local);
        var remoteBefore = await caller.Value(remote);
        await caller.Add(remote, 1);

        Assert.Equal(localBefore, await caller.Value(local));
        Assert.Equal(remoteBefore + 1, await caller.Value(remote));
    }

    private InProcessSiloHandle[] Silos()
        => _fixture.HostedCluster.GetActiveSilos()
            .Select(silo => Assert.IsType<InProcessSiloHandle>(silo))
            .OrderBy(silo => silo.SiloAddress)
            .ToArray();
}
