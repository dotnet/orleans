using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Placement;
using Orleans.Placement.Rebalancing;
using Orleans.Runtime;
using Orleans.Runtime.Configuration.Options;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Rebalancing;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.ActiveRebalancingTests;

[TestCategory("Functional"), TestCategory("ActiveRebalancing")]
public class CustomToleranceTests(CustomToleranceTests.Fixture fixture)
    : TestBase(fixture), IClassFixture<CustomToleranceTests.Fixture>
{
    [Fact]
    public async Task Should_ConvertAllRemoteCalls_ToLocalCalls_WhileRespectingTolerance()
    {
        var e1 = _grainFactory.GetGrain<IE>(1);
        var e2 = _grainFactory.GetGrain<IE>(2);
        var e3 = _grainFactory.GetGrain<IE>(3);

        RequestContext.Set(IPlacementDirector.PlacementHintKey, _silo1);

        await e1.FirstPing(_silo2);
        await e2.FirstPing(_silo2);
        await e3.FirstPing(_silo2);

        RequestContext.Set(IPlacementDirector.PlacementHintKey, _silo2);
        var x = _grainFactory.GetGrain<IX>(0);
        await x.Ping();

        var i = 0;
        while (i < 3)
        {
            await e1.Ping();
            await e2.Ping();
            await e3.Ping();
            await x.Ping();
            i++;
        }

        var f1 = _grainFactory.GetGrain<IF>(1);
        var f2 = _grainFactory.GetGrain<IF>(2);
        var f3 = _grainFactory.GetGrain<IF>(3);

        var e1_host = await e1.GetAddress();
        var e2_host = await e2.GetAddress();
        var e3_host = await e3.GetAddress();

        var f1_host = await f1.GetAddress();
        var f2_host = await f2.GetAddress();
        var f3_host = await f3.GetAddress();

        Assert.Equal(_silo1, e1_host);
        Assert.Equal(_silo1, e2_host);
        Assert.Equal(_silo1, e3_host);

        Assert.Equal(_silo2, f1_host);
        Assert.Equal(_silo2, f2_host);
        Assert.Equal(_silo2, f3_host);

        Assert.Equal(_silo2, await x.GetAddress()); // X remains in silo 2

        await s1_rebalancer.TriggerExchangeRequest();

        do
        {
            e2_host = await e2.GetAddress();
            e3_host = await e3.GetAddress();
            f1_host = await f1.GetAddress();
        }
        while (e2_host == _silo1 || e3_host == _silo1 || f1_host == _silo2);

        await Test();

        // At this point the layout is like follows:

        // S1: E1-F1, sys.svc.clustering.dev, rest (default activations, present in both silos)
        // S2: E2-F2, E3-F3, X, rest (default activations, present in both silos)

        // Tolerance <= 2, and if we ignore defaults once, sys.svc.clustering.dev, and X (which is used to counter-balance sys.svc.clustering.dev)
        // we end up with a total of 2 activations in silo1, and 4 in silo 2, which means the tolerance has been respected, and all remote calls have
        // been converted to local calls: S1: E1-F1, S2: E2-F2, s2: E3-F3.

        // To make sure, we trigger 's1_rebalancer' again, which should yield to no further migrations.
        i = 0;
        while (i < 3)
        {
            await e1.Ping();
            await e2.Ping();
            await e3.Ping();
            await x.Ping();
            i++;
        }

        await s1_rebalancer.TriggerExchangeRequest();
        await Test();

        // To make extra sure, we now trigger 's2_rebalancer', which again should yield to no further migrations.
        i = 0;
        while (i < 3)
        {
            await e1.Ping();
            await e2.Ping();
            await e3.Ping();
            await x.Ping();
            i++;
        }

        await s2_rebalancer.TriggerExchangeRequest();
        await Test();

        //await ResetCounters(); uncomment if you add more tests

        async Task Test()
        {
            e1_host = await e1.GetAddress();
            e2_host = await e2.GetAddress();
            e3_host = await e3.GetAddress();

            f1_host = await f1.GetAddress();
            f2_host = await f2.GetAddress();
            f3_host = await f3.GetAddress();

            Assert.Equal(_silo1, e1_host);  // E1 is still in silo 1
            Assert.Equal(_silo2, e2_host);  // E2 is now in silo 2
            Assert.Equal(_silo2, e3_host);  // E3 is now in silo 2

            Assert.Equal(_silo1, f1_host);  // F1 is now in silo 1
            Assert.Equal(_silo2, f2_host);  // F2 is still in silo 2
            Assert.Equal(_silo2, f3_host);  // F3 is still in silo 2

            Assert.Equal(_silo2, await x.GetAddress()); // X remains in silo 2
        }
    }

    public interface IE : IGrainWithIntegerKey
    {
        Task FirstPing(SiloAddress silo2);
        Task Ping();
        Task<SiloAddress> GetAddress();
    }
    public interface IF : IGrainWithIntegerKey
    {
        Task Ping();
        Task<SiloAddress> GetAddress();
    }
    public interface IX : IGrainWithIntegerKey
    {
        Task Ping();
        Task<SiloAddress> GetAddress();
    }

    public class E : Grain, IE
    {
        public async Task FirstPing(SiloAddress silo2)
        {
            RequestContext.Set(IPlacementDirector.PlacementHintKey, silo2);
            await GrainFactory.GetGrain<IF>(this.GetPrimaryKeyLong()).Ping();
        }

        public Task Ping() => GrainFactory.GetGrain<IF>(this.GetPrimaryKeyLong()).Ping();
        public Task<SiloAddress> GetAddress() => Task.FromResult(GrainContext.Address.SiloAddress);
    }

    public class F : Grain, IF
    {
        public Task Ping() => Task.CompletedTask;
        public Task<SiloAddress> GetAddress() => Task.FromResult(GrainContext.Address.SiloAddress);
    }

    /// <summary>
    /// This is simply to achive initial balance between the 2 silos, as by default the primary
    /// will have 1 more activation than the secondary. That activations is 'sys.svc.clustering.dev'
    /// </summary>
    [Immovable]
    public class X : Grain, IX
    {
        public Task Ping() => Task.CompletedTask;
        public Task<SiloAddress> GetAddress() => Task.FromResult(GrainContext.Address.SiloAddress);
    }

    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 2;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
                => hostBuilder
                    .Configure<SiloMessagingOptions>(o =>
                    {
                        o.AssumeHomogenousSilosForTesting = true;
                        o.ClientGatewayShutdownNotificationTimeout = default;
                    })
                    .Configure<ActiveRebalancingOptions>(o =>
                    {
                        // Make these so that the timers practically never fire! We will invoke the protocol manually.
                        o.MinimumRebalancingDueTime = TimeSpan.FromSeconds(299);
                        o.MaximumRebalancingDueTime = TimeSpan.FromSeconds(300);
                        // Make this practically zero, so we can invoke the protocol more than once without needing to put a delay in the tests. 
                        o.RecoveryPeriod = TimeSpan.FromMilliseconds(1);
                    })
                    .AddActiveRebalancing<HardLimitRule>()
                    .ConfigureServices(service => service.AddSingleton<IRebalancingMessageFilter, TestMessageFilter>());
        }

        private class HardLimitRule : IImbalanceToleranceRule
        {
            public bool IsStatisfiedBy(uint imbalance) => imbalance <= 2;
        }
    }
}