using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Placement;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Repartitioning;
using Orleans.Streams;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.ActivationRepartitioningTests;

/// <summary>
/// Tests for default tolerance scenarios in activation repartitioning, including grain movement and placement decisions.
/// </summary>
// Scenarious can be seen visually here: https://github.com/dotnet/orleans/pull/8877
[TestCategory("Functional"), TestCategory("ActivationRepartitioning")]
public class DefaultToleranceTests(DefaultToleranceTests.Fixture fixture) : RepartitioningTestBase<DefaultToleranceTests.Fixture>(fixture), IClassFixture<DefaultToleranceTests.Fixture>
{
    [Fact]
    public async Task A_ShouldMoveToSilo2__B_And_C_ShouldStayOnSilo2()
    {
        RequestContext.Set(IPlacementDirector.PlacementHintKey, Silo1);

        var scenario = Scenario._1;
        var a = GrainFactory.GetGrain<IA>($"a{scenario}");
        await a.FirstPing(scenario, Silo1, Silo2);

        var i = 0;
        while (i < 3)
        {
            await a.Ping(scenario);
            i++;
        }

        var b = GrainFactory.GetGrain<IB>($"b{scenario}");
        var c = GrainFactory.GetGrain<IC>($"c{scenario}");

        var a_host = await a.GetAddress();
        var b_host = await b.GetAddress();
        var c_host = await c.GetAddress();

        Assert.Equal(Silo1, a_host);
        Assert.Equal(Silo2, b_host);
        Assert.Equal(Silo2, c_host);

        await Silo1Repartitioner.TriggerExchangeRequest();

        do
        {
            a_host = await a.GetAddress();
        }
        while (a_host == Silo1);

        // refresh
        a_host = await a.GetAddress();
        b_host = await b.GetAddress();
        c_host = await c.GetAddress();

        Assert.Equal(Silo2, a_host);  // A is now in silo 2
        Assert.Equal(Silo2, b_host);
        Assert.Equal(Silo2, c_host);

        await ResetCounters();
    }

    [Fact]
    public async Task C_ShouldMoveToSilo1__A_And_B_ShouldStayOnSilo1()
    {
        RequestContext.Set(IPlacementDirector.PlacementHintKey, Silo1);

        var scenario = Scenario._2;
        var a = GrainFactory.GetGrain<IA>($"a{scenario}");
        var b = GrainFactory.GetGrain<IB>($"b{scenario}");

        await a.FirstPing(scenario, Silo1, Silo2);
        await b.Ping(scenario);

        var i = 0;
        while (i < 3)
        {
            await a.Ping(scenario);
            await b.Ping(scenario);
            i++;
        }

        var c = GrainFactory.GetGrain<IC>($"c{scenario}");

        var a_host = await a.GetAddress();
        var b_host = await b.GetAddress();
        var c_host = await c.GetAddress();

        Assert.Equal(Silo1, a_host);
        Assert.Equal(Silo1, b_host);
        Assert.Equal(Silo2, c_host);

        await Silo1Repartitioner.TriggerExchangeRequest();

        do
        {
            c_host = await c.GetAddress();
        }
        while (c_host == Silo2);

        // refresh
        a_host = await a.GetAddress();
        b_host = await b.GetAddress();
        c_host = await c.GetAddress();

        Assert.Equal(Silo1, a_host);
        Assert.Equal(Silo1, b_host);
        Assert.Equal(Silo1, c_host);  // C is now in silo 1

        await ResetCounters();
    }

    [Fact]
    public async Task Immovable_C_ShouldStayOnSilo2__A_And_B_ShouldMoveToSilo2()
    {
        RequestContext.Set(IPlacementDirector.PlacementHintKey, Silo1);

        var scenario = Scenario._3;
        var a = GrainFactory.GetGrain<IA>($"a{scenario}");
        var b = GrainFactory.GetGrain<IB>($"b{scenario}");

        await a.FirstPing(scenario, Silo1, Silo2);
        await b.Ping(scenario);

        var i = 0;
        while (i < 3)
        {
            await a.Ping(scenario);
            await b.Ping(scenario);
            i++;
        }

        var c = GrainFactory.GetGrain<ICImmovable>($"c{scenario}");

        var a_host = await a.GetAddress();
        var b_host = await b.GetAddress();
        var c_host = await c.GetAddress();

        Assert.Equal(Silo1, a_host);
        Assert.Equal(Silo1, b_host);
        Assert.Equal(Silo2, c_host);

        await Silo1Repartitioner.TriggerExchangeRequest();

        do
        {
            a_host = await a.GetAddress();
            b_host = await b.GetAddress();
        }
        while (a_host == Silo1 || b_host == Silo1);

        // refresh
        a_host = await a.GetAddress();
        b_host = await b.GetAddress();
        c_host = await c.GetAddress();

        Assert.Equal(Silo2, a_host);  // A is now in silo 2
        Assert.Equal(Silo2, b_host);  // B is now in silo 2
        Assert.Equal(Silo2, c_host);  // C is still in silo 2

        await ResetCounters();
    }

    [Fact]
    public async Task A_ShouldMoveToSilo2_Or_C_ShouldMoveToSilo1__B_And_D_ShouldStayOnTheirSilos()
    {
        RequestContext.Set(IPlacementDirector.PlacementHintKey, Silo1);

        var scenario = Scenario._4;
        var a = GrainFactory.GetGrain<IA>($"a{scenario}");

        await a.FirstPing(scenario, Silo1, Silo2);

        var i = 0;
        while (i < 3)
        {
            await a.Ping(scenario);
            i++;
        }

        var b = GrainFactory.GetGrain<IB>($"b{scenario}");
        var c = GrainFactory.GetGrain<IC>($"c{scenario}");
        var d = GrainFactory.GetGrain<ID>($"d{scenario}");

        var a_host = await a.GetAddress();
        var b_host = await b.GetAddress();
        var c_host = await c.GetAddress();
        var d_host = await d.GetAddress();

        Assert.Equal(Silo1, a_host);
        Assert.Equal(Silo1, b_host);
        Assert.Equal(Silo2, c_host);
        Assert.Equal(Silo2, d_host);

        await Silo1Repartitioner.TriggerExchangeRequest();

        do
        {
            a_host = await a.GetAddress();
            c_host = await c.GetAddress();
        }
        while (a_host == Silo1 && c_host == Silo2);

        // refresh
        a_host = await a.GetAddress();
        b_host = await b.GetAddress();
        c_host = await c.GetAddress();
        d_host = await d.GetAddress();

        // A can go to Silo 2, or C can come to Silo 1, both are valid, so we need to check for both.
        if (a_host == Silo2 && c_host == Silo2)
        {
            Assert.Equal(Silo2, a_host);  // A is now in silo 2
            Assert.Equal(Silo1, b_host);
            Assert.Equal(Silo2, c_host);
            Assert.Equal(Silo2, d_host);

            return;
        }

        if (a_host == Silo1 && c_host == Silo1)
        {
            Assert.Equal(Silo1, a_host);
            Assert.Equal(Silo1, b_host);
            Assert.Equal(Silo1, c_host);  // C is now in silo 1
            Assert.Equal(Silo2, d_host);

            return;
        }

        await ResetCounters();
    }

    [SkippableFact]
    public async Task Receivers_ShouldMoveCloseTo_PullingAgent()
    {
        var sp1 = GrainFactory.GetGrain<ISP>("s1");
        var sp2 = GrainFactory.GetGrain<ISP>("s2");
        var sp3 = GrainFactory.GetGrain<ISP>("s3");

        RequestContext.Set(IPlacementDirector.PlacementHintKey, Silo1);
        await sp1.FirstPing();
        await sp2.FirstPing();

        RequestContext.Set(IPlacementDirector.PlacementHintKey, Silo2);
        await sp3.FirstPing();

        var i = 0;
        while (i < 3)
        {
            await sp1.StreamPing();
            await sp2.StreamPing();
            await sp3.StreamPing();
            i++;
        }

        var sr1 = GrainFactory.GetGrain<ISR>("s1");
        var sr2 = GrainFactory.GetGrain<ISR>("s2");
        var sr3 = GrainFactory.GetGrain<ISR>("s3");

        var sr1_GotHit = false;
        var sr2_GotHit = false;
        var sr3_GotHit = false;

        while (!sr1_GotHit || !sr2_GotHit || !sr3_GotHit)
        {
            sr1_GotHit = await sr1.GotStreamHit();
            sr2_GotHit = await sr2.GotStreamHit();
            sr3_GotHit = await sr3.GotStreamHit();
        }

        var sr1_host = await sr1.GetAddress();
        var sr2_host = await sr2.GetAddress();
        var sr3_host = await sr3.GetAddress();

        Assert.Equal(Silo1, sr1_host);
        Assert.Equal(Silo1, sr2_host);
        Assert.Equal(Silo2, sr3_host);

        await Silo1Repartitioner.TriggerExchangeRequest();
        await Task.Delay(100); // leave some breathing room - may not be enough though, thats why this test is skippable

        var allowedDuration = TimeSpan.FromSeconds(3);
        Stopwatch stopwatch = new();
        stopwatch.Start();

        do
        {
            sr1_host = await sr1.GetAddress();
            sr2_host = await sr2.GetAddress();

            Skip.If(stopwatch.Elapsed > allowedDuration);
        }
        while (sr1_host == Silo1 || sr2_host == Silo1);

        // refresh
        sr1_host = await sr1.GetAddress();
        sr2_host = await sr2.GetAddress();
        sr3_host = await sr3.GetAddress();

        Assert.Equal(Silo2, sr1_host);  // SR1 is now in silo 2, as there is 1 pulling agent (which is moved to silo 2 by the streaming runtime)
        Assert.Equal(Silo2, sr2_host);  // SR2 is now in silo 2, as there is 1 pulling agent (which is moved to silo 2 by the streaming runtime)
        Assert.Equal(Silo2, sr3_host);

        await ResetCounters();
    }

    public enum Scenario { _1, _2, _3, _4 }

    public interface IBase : IGrainWithStringKey
    {
        Task Ping(Scenario scenario);
        Task<SiloAddress> GetAddress();
    }
    public interface IA : IBase
    {
        Task FirstPing(Scenario scenario, SiloAddress silo1, SiloAddress silo2);
    }
    public interface IB : IBase { }
    public interface IC : IBase
    {
        Task Ping(Scenario scenario, SiloAddress silo2);
    }
    public interface ICImmovable : IBase { }
    public interface ID : IBase { }
    public interface ISP : IGrainWithStringKey
    {
        Task FirstPing();
        Task StreamPing();
        Task<SiloAddress> GetAddress();
    }
    public interface ISR : IGrainWithStringKey
    {
        Task Ping();
        Task<bool> GotStreamHit();
        Task<SiloAddress> GetAddress();
    }

    public abstract class GrainBase : Grain
    {
        public Task<SiloAddress> GetAddress() => Task.FromResult(GrainContext.Address.SiloAddress);
    }

    public class A : GrainBase, IA
    {
        private SiloAddress _silo1;
        private SiloAddress _silo2;

        public async Task FirstPing(Scenario scenario, SiloAddress silo1, SiloAddress silo2)
        {
            _silo1 = silo1;
            _silo2 = silo2;

            switch (scenario)
            {
                case Scenario._1:
                    {
                        RequestContext.Set(IPlacementDirector.PlacementHintKey, _silo2);

                        await GrainFactory.GetGrain<IB>($"b{scenario}").Ping(scenario);
                        await GrainFactory.GetGrain<IC>($"c{scenario}").Ping(scenario);
                    }
                    break;
                case Scenario._2:
                    {
                        RequestContext.Set(IPlacementDirector.PlacementHintKey, _silo2);
                        await GrainFactory.GetGrain<IC>($"c{scenario}").Ping(scenario);
                    }
                    break;
                case Scenario._3:
                    {
                        RequestContext.Set(IPlacementDirector.PlacementHintKey, _silo2);
                        await GrainFactory.GetGrain<ICImmovable>($"c{scenario}").Ping(scenario);
                    }
                    break;
                case Scenario._4:
                    {
                        RequestContext.Set(IPlacementDirector.PlacementHintKey, _silo1);
                        await GrainFactory.GetGrain<IB>($"b{scenario}").Ping(scenario);

                        RequestContext.Set(IPlacementDirector.PlacementHintKey, _silo2);
                        await GrainFactory.GetGrain<IC>($"c{scenario}").Ping(scenario);
                        await GrainFactory.GetGrain<IC>($"c{scenario}").Ping(scenario, _silo2);
                    }
                    break;
                default: throw new NotSupportedException();
            }
        }

        public async Task Ping(Scenario scenario)
        {
            switch (scenario)
            {
                case Scenario._1:
                    {
                        await GrainFactory.GetGrain<IB>($"b{scenario}").Ping(scenario);
                        await GrainFactory.GetGrain<IC>($"c{scenario}").Ping(scenario);
                    }
                    break;
                case Scenario._2:
                    {
                        await GrainFactory.GetGrain<IC>($"c{scenario}").Ping(scenario);
                    }
                    break;
                case Scenario._3:
                    {
                        await GrainFactory.GetGrain<ICImmovable>($"c{scenario}").Ping(scenario);
                    }
                    break;
                case Scenario._4:
                    {
                        await GrainFactory.GetGrain<IB>($"b{scenario}").Ping(scenario);
                        await GrainFactory.GetGrain<IC>($"c{scenario}").Ping(scenario);
                        await GrainFactory.GetGrain<IC>($"c{scenario}").Ping(scenario, _silo2);
                    }
                    break;
                default: throw new NotSupportedException();
            }
        }
    }

    public class B : GrainBase, IB
    {
        public Task Ping(Scenario scenario) =>
            scenario switch
            {
                Scenario._1 => Task.CompletedTask,
                Scenario._2 => GrainFactory.GetGrain<IC>($"c{scenario}").Ping(scenario),
                Scenario._3 => GrainFactory.GetGrain<ICImmovable>($"c{scenario}").Ping(scenario),
                Scenario._4 => Task.CompletedTask,
                _ => throw new NotSupportedException(),
            };
    }

    public class C : GrainBase, IC
    {
        public Task Ping(Scenario scenario) =>
            scenario switch
            {
                Scenario._1 => GrainFactory.GetGrain<IB>($"b{scenario}").Ping(scenario),
                Scenario._2 => Task.CompletedTask,
                Scenario._3 => Task.CompletedTask,
                Scenario._4 => Task.CompletedTask,
                _ => throw new NotSupportedException(),
            };

        public async Task Ping(Scenario scenario, SiloAddress silo2)
        {
            switch (scenario)
            {
                case Scenario._4:
                    {
                        RequestContext.Set(IPlacementDirector.PlacementHintKey, silo2);
                        await GrainFactory.GetGrain<ID>($"d{scenario}").Ping(scenario);
                    }
                    break;
                default: throw new NotSupportedException();
            }
        }
    }

    [Immovable(ImmovableKind.Repartitioner)]
    public class CImmovable : GrainBase, ICImmovable
    {
        public Task Ping(Scenario scenario) =>
            scenario switch
            {
                Scenario._3 => Task.CompletedTask,
                _ => throw new NotSupportedException(),
            };
    }

    public class D : GrainBase, ID
    {
        public Task Ping(Scenario scenario) =>
            scenario switch
            {
                Scenario._4 => Task.CompletedTask,
                _ => throw new NotSupportedException(),
            };
    }

    [Immovable(ImmovableKind.Repartitioner)]
    public class SP : GrainBase, ISP
    {
        // We are just 'Immovable' on this type, because we just want it to push messages to the stream,
        // as for some reason pushing to a stream via the cluster client isnt invoking the consumer grains.

        private IAsyncStream<int> _stream;

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            _stream = this.GetStreamProvider(Fixture.StreamProviderName)
                .GetStream<int>(StreamId.Create(Fixture.StreamNamespaceName, this.GetPrimaryKeyString()));

            return Task.CompletedTask;
        }

        public Task FirstPing() => GrainFactory.GetGrain<ISR>(this.GetPrimaryKeyString()).Ping();
        public Task StreamPing() => _stream.OnNextAsync(Random.Shared.Next());
    }

    [ImplicitStreamSubscription(Fixture.StreamNamespaceName)]
    public class SR : GrainBase, ISR
    {
        private bool _streamHit = false;

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            var sp = this.GetStreamProvider(Fixture.StreamProviderName)
                .GetStream<int>(StreamId.Create(Fixture.StreamNamespaceName, this.GetPrimaryKeyString()));

            await sp.SubscribeAsync((_, _) =>
             {
                 _streamHit = true;
                 return Task.CompletedTask;
             });
        }

        public Task Ping() => Task.CompletedTask;
        public Task<bool> GotStreamHit() => Task.FromResult(_streamHit);
    }

    public class Fixture : BaseTestClusterFixture
    {
        public const string StreamProviderName = "arsp";
        public const string StreamNamespaceName = "arns";

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 2;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
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
                        // Make this practically zero, so we can invoke the protocol more than once without needing to put a delay in the tests. 
                        o.RecoveryPeriod = TimeSpan.FromMilliseconds(1);
                    })
                    .AddMemoryStreams(StreamProviderName, c =>
                    {
                        c.ConfigurePartitioning(1);
                        c.ConfigureStreamPubSub(StreamPubSubType.ImplicitOnly);
                    })
                    .AddActivationRepartitioner()
                    .ConfigureServices(service => service.AddSingleton<IRepartitionerMessageFilter, TestMessageFilter>());
#pragma warning restore ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        }
    }
}