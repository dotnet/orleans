using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Placement;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Repartitioning;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.ActivationRepartitioningTests;

// Scenarios can be seen visually here: https://github.com/dotnet/orleans/pull/8877
[TestCategory("Functional"), TestCategory("ActivationRepartitioning"), Category("BVT")]
public class CustomToleranceTests(CustomToleranceTests.Fixture fixture, ITestOutputHelper output) : RepartitioningTestBase<CustomToleranceTests.Fixture>(fixture), IClassFixture<CustomToleranceTests.Fixture>
{
    [Fact]
    public async Task Should_ConvertAllRemoteCalls_ToLocalCalls_WhileRespectingTolerance()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await AdjustActivationCountOffsets();

        var e1 = GrainFactory.GetGrain<IE>(1);
        var e2 = GrainFactory.GetGrain<IE>(2);
        var e3 = GrainFactory.GetGrain<IE>(3);

        RequestContext.Set(IPlacementDirector.PlacementHintKey, Silo1);

        await e1.FirstPing(Silo2);
        await e2.FirstPing(Silo2);
        await e3.FirstPing(Silo2);

        RequestContext.Set(IPlacementDirector.PlacementHintKey, Silo2);
        var x = GrainFactory.GetGrain<IX>(0);
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

        var f1 = GrainFactory.GetGrain<IF>(1);
        var f2 = GrainFactory.GetGrain<IF>(2);
        var f3 = GrainFactory.GetGrain<IF>(3);

        var e1_host = await e1.GetAddress();
        var e2_host = await e2.GetAddress();
        var e3_host = await e3.GetAddress();

        var f1_host = await f1.GetAddress();
        var f2_host = await f2.GetAddress();
        var f3_host = await f3.GetAddress();

        Assert.Equal(Silo1, e1_host);
        Assert.Equal(Silo1, e2_host);
        Assert.Equal(Silo1, e3_host);

        Assert.Equal(Silo2, f1_host);
        Assert.Equal(Silo2, f2_host);
        Assert.Equal(Silo2, f3_host);

        Assert.Equal(Silo2, await x.GetAddress()); // X remains in silo 2

        await Silo1Repartitioner.TriggerExchangeRequest();

        // At this point the layout is like follows:

        // S1: E1-F1, E3-F3, sys.svc.clustering.dev, rest (default activations, present in both silos)
        // S2: E2-F2, X, rest (default activations, present in both silos)

        // Tolerance <= 2, and if we ignore defaults once, sys.svc.clustering.dev, and X (which is used to counter-balance sys.svc.clustering.dev)
        // we end up with a total of 4 activations in silo1, and 2 in silo 2, which means the tolerance has been respected, and all remote calls have
        // been converted to local calls.

        await Test();

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

        await LogEdgesAsync(Silo1Repartitioner);
        await LogEdgesAsync(Silo2Repartitioner);
        await Silo1Repartitioner.TriggerExchangeRequest();
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

        await Silo2Repartitioner.TriggerExchangeRequest();

        await Test();

        async Task Test()
        {
            e1_host = await e1.GetAddress();
            e2_host = await e2.GetAddress();
            e3_host = await e3.GetAddress();

            f1_host = await f1.GetAddress();
            f2_host = await f2.GetAddress();
            f3_host = await f3.GetAddress();

            // Check that each grain is collocated with its pair
            Assert.Equal(f1_host, e1_host);
            Assert.Equal(f2_host, e2_host);
            Assert.Equal(f3_host, e3_host);

            var locations = new SiloAddress[] { e1_host, e2_host, e3_host };
            Assert.False(locations.All(h => h.Equals(Silo1)), "Grains should not all be located on silo 1.");
            Assert.False(locations.All(h => h.Equals(Silo2)), "Grains should not all be located on silo 2.");

            Assert.Equal(Silo2, await x.GetAddress()); // X remains in silo 2
        }
    }

    private async Task LogEdgesAsync(IActivationRepartitionerSystemTarget repartitioner)
    {
        var edgeCounts = await repartitioner.GetGrainCallFrequencies();
        output.WriteLine($"{repartitioner.GetGrainId()} call frequencies:");
        foreach (var (edge, freq) in edgeCounts)
        {
            output.WriteLine($"\t{edge} x {freq}");
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
        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            ServiceProvider.GetRequiredService<ILogger<E>>().LogInformation("Activating {GrainId} on silo {SiloAddress}", this.GrainId, this.Runtime.SiloAddress);
            return base.OnActivateAsync(cancellationToken);
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            ServiceProvider.GetRequiredService<ILogger<E>>().LogInformation("Deactivating {GrainId} on silo {SiloAddress}. Reason: {Reason}", this.GrainId, this.Runtime.SiloAddress, reason);
            return base.OnDeactivateAsync(reason, cancellationToken);
        }
    }

    public class F : Grain, IF
    {
        public Task Ping() => Task.CompletedTask;

        public Task<SiloAddress> GetAddress() => Task.FromResult(GrainContext.Address.SiloAddress);

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            ServiceProvider.GetRequiredService<ILogger<F>>().LogInformation("Activating {GrainId} on silo {SiloAddress}", this.GrainId, this.Runtime.SiloAddress);
            return base.OnActivateAsync(cancellationToken);
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            ServiceProvider.GetRequiredService<ILogger<F>>().LogInformation("Deactivating {GrainId} on silo {SiloAddress}. Reason: {Reason}", this.GrainId, this.Runtime.SiloAddress, reason);
            return base.OnDeactivateAsync(reason, cancellationToken);
        }
    }

    /// <summary>
    /// This is simply to achieve initial balance between the 2 silos, as by default the primary
    /// will have 1 more activation than the secondary. That activations is 'sys.svc.clustering.dev'
    /// </summary>
    [Immovable(ImmovableKind.Repartitioner)]
    public class X : Grain, IX
    {
        public Task Ping() => Task.CompletedTask;
        public Task<SiloAddress> GetAddress() => Task.FromResult(GrainContext.Address.SiloAddress);

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            ServiceProvider.GetRequiredService<ILogger<X>>().LogInformation("Activating {GrainId} on silo {SiloAddress}", this.GrainId, this.Runtime.SiloAddress);
            return base.OnActivateAsync(cancellationToken);
        }

        public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            ServiceProvider.GetRequiredService<ILogger<X>>().LogInformation("Deactivating {GrainId} on silo {SiloAddress}. Reason: {Reason}", this.GrainId, this.Runtime.SiloAddress, reason);
            return base.OnDeactivateAsync(reason, cancellationToken);
        }
    }

    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 2;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            builder.AddClientBuilderConfigurator<ClientConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
#pragma warning disable ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                => hostBuilder
                    .Configure<SiloMessagingOptions>(o =>
                    {
                        o.AssumeHomogenousSilosForTesting = true;
                        o.ClientGatewayShutdownNotificationTimeout = default;
                    })
                    .Configure<ActivationRepartitionerOptions>(o =>
                    {
                        // Make these so that the timers practically never fire! We will invoke the protocol manually.
                        o.MinRoundPeriod = TimeSpan.FromSeconds(299);
                        o.MaxRoundPeriod = TimeSpan.FromSeconds(300);
                        // Make this zero, so we can invoke the protocol more than once without needing to put a delay in the tests. 
                        o.RecoveryPeriod = TimeSpan.Zero;

                        // To remove the remote possibility of false positives caused by the probabilistic filtering in tests.
                        o.AnchoringFilterEnabled = false;
                    })
                    .AddActivationRepartitioner<HardLimitRule>()
                    .ConfigureLogging(logging => logging.AddFilter("Orleans.Runtime.Placement.Repartitioning", LogLevel.Trace))
                    .ConfigureServices(service => service.AddSingleton<IRepartitionerMessageFilter, TestMessageFilter>());
#pragma warning restore ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        }

        private class HardLimitRule : IImbalanceToleranceRule
        {
            public bool IsSatisfiedBy(uint imbalance) => imbalance <= 2;
        }

        private class ClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.Configure<GatewayOptions>(o => o.PreferredGatewayIndex = 0);
            }
        }
    }
}