using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Core.Internal;
using Orleans.Placement.Rebalancing;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;
using SPFixture = UnitTests.ActivationRebalancingTests.StatePreservationRebalancingTests.StatePreservationFixture;

#nullable enable

namespace UnitTests.ActivationRebalancingTests;

/// <summary>
/// Tests for activation rebalancing with state preservation when the hosting silo dies.
/// </summary>
[TestCategory("Functional"), TestCategory("ActivationRebalancing")]
public class StatePreservationRebalancingTests(SPFixture fixture, ITestOutputHelper output)
    : RebalancingTestBase<SPFixture>(fixture, output), IClassFixture<SPFixture>
{
    private const string ErrorMessage =
        "The rebalancer was not found in any of the 4 silos. " +
        "Either you have added more silos and not updated this code, " +
        "or there is a bug in the rebalancer or monitor";

    [Fact]
    public async Task Should_Migrate_And_Preserve_State_When_Hosting_Silo_Dies()
    {
        var tasks = new List<Task>();

        // Move the rebalancer to the first secondary silo, since we will stop it later and we cannot stop
        // the primary in this test setup.
        RequestContext.Set(IPlacementDirector.PlacementHintKey, Cluster.Silos[1].SiloAddress);
        await Cluster.Client.GetGrain<IActivationRebalancerWorker>(0).Cast<IGrainManagementExtension>().MigrateOnIdle();
        RequestContext.Remove(IPlacementDirector.PlacementHintKey);

        AddTestActivations(tasks, Silo1, 300);
        AddTestActivations(tasks, Silo2, 30);
        AddTestActivations(tasks, Silo3, 180);
        AddTestActivations(tasks, Silo4, 100);

        await Task.WhenAll(tasks);

        var stats = await MgmtGrain.GetDetailedGrainStatistics();

        var initialSilo1Activations = GetActivationCount(stats, Silo1);
        var initialSilo2Activations = GetActivationCount(stats, Silo2);
        var initialSilo3Activations = GetActivationCount(stats, Silo3);
        var initialSilo4Activations = GetActivationCount(stats, Silo4);

        OutputHelper.WriteLine(
           $"Pre-rebalancing activations:\n" +
           $"Silo1: {initialSilo1Activations}\n" +
           $"Silo2: {initialSilo2Activations}\n" +
           $"Silo3: {initialSilo3Activations}\n" +
           $"Silo4: {initialSilo4Activations}\n");

        var silo1Activations = initialSilo1Activations;
        var silo2Activations = initialSilo2Activations;
        var silo3Activations = initialSilo3Activations;
        var silo4Activations = initialSilo4Activations;

        var rebalancerHostNum = 0;
        var index = 0;

        while (index < 6)
        {
            if (index == 3)
            {
                (var rebalancerHost, rebalancerHostNum) = await FindRebalancerHost(Silo1);

                OutputHelper.WriteLine($"Cycle {index}: Now stopping Silo{rebalancerHostNum}, which is the host of the rebalancer\n");

                Assert.NotEqual(rebalancerHost, Cluster.Silos[0].SiloAddress);
                await Cluster.StopSiloAsync(Cluster.Silos.First(x => x.SiloAddress.Equals(rebalancerHost)));
            }

            await Task.Delay(SPFixture.SessionCyclePeriod);
            stats = await MgmtGrain.GetDetailedGrainStatistics();

            silo1Activations = GetActivationCount(stats, Silo1);
            silo2Activations = GetActivationCount(stats, Silo2);
            silo3Activations = GetActivationCount(stats, Silo3);
            silo4Activations = GetActivationCount(stats, Silo4);

            index++;
        }

        if (rebalancerHostNum == 1)
        {
            Assert.True(silo2Activations > initialSilo2Activations,
                $"Did not expect Silo2 to have less activations than what it started with: " +
                $"[{initialSilo2Activations} -> {silo2Activations}]");

            Assert.True(silo3Activations < initialSilo3Activations,
                $"Did not expect Silo3 to have more activations than what it started with: " +
                $"[{initialSilo3Activations} -> {silo3Activations}]");
        }
        else if (rebalancerHostNum == 2)
        {
            Assert.True(silo3Activations < initialSilo3Activations,
                $"Did not expect Silo3 to have more activations than what it started with: " +
                $"[{initialSilo3Activations} -> {silo3Activations}]");

            Assert.True(silo4Activations > initialSilo4Activations,
                $"Did not expect Silo4 to have less activations than what it started with: " +
                $"[{initialSilo4Activations} -> {silo4Activations}]");
        }
        else if (rebalancerHostNum == 3)
        {
            Assert.True(silo1Activations < initialSilo1Activations,
                $"Did not expect Silo1 to have more activations than what it started with: " +
                $"[{initialSilo1Activations} -> {silo1Activations}]");

            Assert.True(silo2Activations > initialSilo2Activations,
                $"Did not expect Silo2 to have less activations than what it started with: " +
                $"[{initialSilo2Activations} -> {silo2Activations}]");
        }
        else if (rebalancerHostNum == 4)
        {
            Assert.True(silo1Activations < initialSilo1Activations,
                $"Did not expect Silo1 to have more activations than what it started with: " +
                $"[{initialSilo1Activations} -> {silo1Activations}]");

            Assert.True(silo2Activations > initialSilo2Activations,
                $"Did not expect Silo2 to have less activations than what it started with: " +
                $"[{initialSilo2Activations} -> {silo2Activations}]");
        }

        OutputHelper.WriteLine(
            $"Post-rebalancing activations ({index} cycles):\n" +
            $"Silo1: {(rebalancerHostNum == 1 ? "DEAD" : silo1Activations)}\n" +
            $"Silo2: {(rebalancerHostNum == 2 ? "DEAD" : silo2Activations)}\n" +
            $"Silo3: {(rebalancerHostNum == 3 ? "DEAD" : silo3Activations)}\n" +
            $"Silo4: {(rebalancerHostNum == 4 ? "DEAD" : silo4Activations)}\n");

        (_, rebalancerHostNum) = await FindRebalancerHost(rebalancerHostNum switch
        {
            1 => Silo2,
            2 => Silo3,
            3 => Silo4,
            4 => Silo1,
            _ => throw new InvalidOperationException(ErrorMessage)
        });

        OutputHelper.WriteLine($"The rebalancer is hosted by Silo{rebalancerHostNum} now");
    }

    private async Task<(SiloAddress, int)> FindRebalancerHost(SiloAddress target)
    {
        var host = (await GrainFactory
            .GetSystemTarget<IActivationRebalancerMonitor>(
             Constants.ActivationRebalancerMonitorType, target)
            .GetRebalancingReport(true))
            .Host;

        if (host.Equals(Silo1))
        {
            return new(host, 1);
        }

        if (host.Equals(Silo2))
        {
            return new(host, 2);
        }

        if (host.Equals(Silo3))
        {
            return new(host, 3);
        }

        if (host.Equals(Silo4))
        {
            return new(host, 4);
        }

        Assert.Fail(ErrorMessage);
        return new(SiloAddress.Zero, 0);
    }

    public class StatePreservationFixture : BaseInProcessTestClusterFixture
    {
        public static readonly TimeSpan RebalancerDueTime = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan SessionCyclePeriod = TimeSpan.FromSeconds(3);

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.Options.UseRealEnvironmentStatistics = true;
            builder.ConfigureSilo((options, siloBuilder)
#pragma warning disable ORLEANSEXP002
                => siloBuilder
                    .Configure<SiloMessagingOptions>(o =>
                    {
                        o.ResponseTimeoutWithDebugger = TimeSpan.FromMinutes(1);
                        o.AssumeHomogenousSilosForTesting = true;
                        o.ClientGatewayShutdownNotificationTimeout = default;
                    })
                    .Configure<ActivationRebalancerOptions>(o =>
                    {
                        o.RebalancerDueTime = RebalancerDueTime;
                        o.SessionCyclePeriod = SessionCyclePeriod;
                    })
                    .AddActivationRebalancer());
#pragma warning restore ORLEANSEXP002
        }
    }
}
