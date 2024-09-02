using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using CsCheck;
using Xunit.Abstractions;
using Microsoft.Extensions.Configuration;
using Orleans.Placement;

namespace UnitTests.ActivationRebalancingTests;

[TestCategory("Functional"), TestCategory("ActivationRebalancing")]
public class RebalancingTests : BaseTestClusterFixture, IClassFixture<RebalancingTests.Fixture>
{
    private const long _10MB = 10_048_576;
    private const long _5MB = 5_242_880;

    private readonly SiloAddress _silo1;
    private readonly SiloAddress _silo2;
    private readonly SiloAddress _silo3;
    private readonly SiloAddress _silo4;

    private readonly ITestOutputHelper _output;
    private readonly IGrainFactory _grainFactory;
    private readonly IManagementGrain _mgmtGrain;
    private readonly IInternalActivationRebalancerGrain _rebalancerGrain;

    public RebalancingTests(Fixture fixture, ITestOutputHelper output)
    {
        var silos = fixture.HostedCluster.GetActiveSilos().Select(h => h.SiloAddress).OrderBy(s => s).ToArray();

        _silo1 = silos[0];
        _silo2 = silos[1];
        _silo3 = silos[2];
        _silo4 = silos[3];

        _output = output;
        _grainFactory = fixture.HostedCluster.GrainFactory;
        _mgmtGrain = _grainFactory.GetGrain<IManagementGrain>(0);
        _rebalancerGrain = _grainFactory.GetGrain<IInternalActivationRebalancerGrain>(IActivationRebalancerGrain.Key);
    }

    private static ulong GetActivationCount(DetailedGrainStatistic[] stats, SiloAddress silo) =>
        (ulong)stats.Count(x => x.SiloAddress.Equals(silo));

    [Fact]
    public async Task Silo1_Should_Disperse_Activations_Silo2_Should_Acquire()
    {
        await _mgmtGrain.ForceActivationCollection(TimeSpan.Zero);

        var tasks = new List<Task>();

        RequestContext.Set(IPlacementDirector.PlacementHintKey, _silo1);
        for (var i = 0; i < 100; i++)
        {
            tasks.Add(_grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }

        RequestContext.Set(IPlacementDirector.PlacementHintKey, _silo2);
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(_grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }

        await Task.WhenAll(tasks);

        var stats = await _mgmtGrain.GetDetailedGrainStatistics();

        var initialSilo1Activations = GetActivationCount(stats, _silo1);
        var initialSilo2Activations = GetActivationCount(stats, _silo2);

        var silo1Activations = initialSilo1Activations;
        var silo2Activations = initialSilo2Activations;

        var index = 0;
        while (index < 3)
        {
            await Task.Delay(Fixture.SessionCyclePeriod);
            stats = await _mgmtGrain.GetDetailedGrainStatistics();

            silo1Activations = GetActivationCount(stats, _silo1);
            silo2Activations = GetActivationCount(stats, _silo2);

            index++;
        }

        Assert.True(silo1Activations < initialSilo1Activations,
            $"Did not expect Silo1 to have more activations than what it started with: " +
            $"[{initialSilo1Activations} -> {silo1Activations}]");

        Assert.True(silo2Activations > initialSilo2Activations,
            "Did not expect Silo2 to have less activations than what it started with: " +
            $"[{initialSilo2Activations} -> {silo2Activations}]");

        _output.WriteLine(
           $"{nameof(Silo1_Should_Disperse_Activations_Silo2_Should_Acquire)} test resulted in " +
           $"{initialSilo1Activations - silo1Activations} activations being moved from Silo1 -> Silo2");
    }

    [Fact]
    public async Task Should_Move_Activations_From_Silo1_And_Silo3_To_Silo2_And_Silo4()
    {
        await _mgmtGrain.ForceActivationCollection(TimeSpan.Zero);

        var tasks = new List<Task>();

        RequestContext.Set(IPlacementDirector.PlacementHintKey, _silo1);
        for (var i = 0; i < 300; i++)
        {
            tasks.Add(_grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }

        RequestContext.Set(IPlacementDirector.PlacementHintKey, _silo2);
        for (var i = 0; i < 30; i++)
        {
            tasks.Add(_grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }

        RequestContext.Set(IPlacementDirector.PlacementHintKey, _silo3);
        for (var i = 0; i < 175; i++)
        {
            tasks.Add(_grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }

        RequestContext.Set(IPlacementDirector.PlacementHintKey, _silo4);
        for (var i = 0; i < 100; i++)
        {
            tasks.Add(_grainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }

        await Task.WhenAll(tasks);

        var stats = await _mgmtGrain.GetDetailedGrainStatistics();

        var initialSilo1Activations = GetActivationCount(stats, _silo1);
        var initialSilo2Activations = GetActivationCount(stats, _silo2);
        var initialSilo3Activations = GetActivationCount(stats, _silo3);
        var initialSilo4Activations = GetActivationCount(stats, _silo4);

        _output.WriteLine(
           $"Pre-rebalancing activations:\n" +
           $"Silo1: {initialSilo1Activations}\n" +
           $"Silo2: {initialSilo2Activations}\n" +
           $"Silo3: {initialSilo3Activations}\n" +
           $"Silo4: {initialSilo4Activations}\n");

        var silo1Activations = initialSilo1Activations;
        var silo2Activations = initialSilo2Activations;
        var silo3Activations = initialSilo3Activations;
        var silo4Activations = initialSilo4Activations;

        var index = 0;
        while (index < 6)
        {
            await Task.Delay(Fixture.SessionCyclePeriod);
            stats = await _mgmtGrain.GetDetailedGrainStatistics();

            silo1Activations = GetActivationCount(stats, _silo1);
            silo2Activations = GetActivationCount(stats, _silo2);
            silo3Activations = GetActivationCount(stats, _silo3);
            silo4Activations = GetActivationCount(stats, _silo4);

            index++;
        }

        Assert.True(silo1Activations < initialSilo1Activations,
            $"Did not expect Silo1 to have more activations than what it started with: " +
            $"[{initialSilo1Activations} -> {silo1Activations}]");

        Assert.True(silo2Activations > initialSilo2Activations,
            $"Did not expect Silo2 to have less activations than what it started with: " +
            $"[{initialSilo2Activations} -> {silo2Activations}]");

        Assert.True(silo3Activations < initialSilo3Activations,
            $"Did not expect Silo3 to have more activations than what it started with: " +
            $"[{initialSilo3Activations} -> {silo3Activations}]");

        Assert.True(silo4Activations > initialSilo4Activations,
            "Did not expect Silo4 to have less activations than what it started with: " +
            $"[{initialSilo4Activations} -> {silo4Activations}]");

        _output.WriteLine(
            $"Post-rebalancing activations:\n" +
            $"Silo1: {silo1Activations}\n" +
            $"Silo2: {silo2Activations}\n" +
            $"Silo3: {silo3Activations}\n" +
            $"Silo4: {silo4Activations}\n");

        //_output.WriteLine(
        //    $"{initialSilo1Activations - silo1Activations} activations have been moved from Silo1 -> Silo2\n" +
        //    $"{initialSilo3Activations - silo3Activations} activations have been moved from Silo3 -> Silo4");
    }

    public interface IRebalancingTestGrain : IGrainWithGuidKey
    {
        Task Ping();
    }

    public class RebalancingTestGrain : Grain, IRebalancingTestGrain
    {
        public Task Ping() => Task.CompletedTask;
    }

    public class Fixture : BaseTestClusterFixture
    {
        public static readonly TimeSpan RebalancerDueTime = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan SessionCyclePeriod = TimeSpan.FromSeconds(5);

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.Options.UseRealEnvironmentStatistics = true;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
#pragma warning disable ORLEANSEXP002
                => hostBuilder
                    .Configure<SiloMessagingOptions>(o =>
                    {
                        o.AssumeHomogenousSilosForTesting = true;
                        o.ClientGatewayShutdownNotificationTimeout = default;
                    })
                    .Configure<ActivationRebalancerOptions>(o =>
                    {
                        o.RebalancerDueTime = RebalancerDueTime;
                        o.SessionCyclePeriod = SessionCyclePeriod;
                    })
                    .AddActivationRebalancer();
#pragma warning restore ORLEANSEXP002
        }
    }
}