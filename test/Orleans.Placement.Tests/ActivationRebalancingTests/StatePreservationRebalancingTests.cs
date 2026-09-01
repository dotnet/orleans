using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Core.Internal;
using Orleans.Placement.Rebalancing;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using SPFixture = UnitTests.ActivationRebalancingTests.StatePreservationRebalancingTests.StatePreservationFixture;

#nullable enable

namespace UnitTests.ActivationRebalancingTests;

/// <summary>
/// Tests for activation rebalancing with state preservation when the hosting silo dies.
/// </summary>
[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("Placement")]
[TestCategory("Functional"), TestCategory("ActivationRebalancing")]
public class StatePreservationRebalancingTests(SPFixture fixture, ITestOutputHelper output)
    : RebalancingTestBase<SPFixture>(fixture, output), IClassFixture<SPFixture>
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    private const string ErrorMessage =
        "The rebalancer was not found in any of the 4 silos. " +
        "Either you have added more silos and not updated this code, " +
        "or there is a bug in the rebalancer or monitor";

    [Fact]
    public async Task Should_Migrate_And_Preserve_State_When_Hosting_Silo_Dies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tasks = new List<Task>();
        using var rebalancerEvents = RebalancerDiagnosticObserver.Create(Cluster);
        var rebalancer = Cluster.Client!.GetGrain<IActivationRebalancerWorker>(0);
        var targetHost = Cluster.Silos[1].SiloAddress;

        // Move the rebalancer to the first secondary silo, since we will stop it later and we cannot stop
        // the primary in this test setup.
        await MoveRebalancerToSilo(rebalancer, targetHost);

        AddTestActivations(tasks, Silo1, 300);
        AddTestActivations(tasks, Silo2, 30);
        AddTestActivations(tasks, Silo3, 180);
        AddTestActivations(tasks, Silo4, 100);

        await Task.WhenAll(tasks);

        rebalancerEvents.Clear();
        await rebalancerEvents.WaitForCycleCountAsync(targetHost, 3, WaitTimeout);

        var stats = await MgmtGrain.GetDetailedGrainStatistics(null, null, cancellationToken);

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

        (var rebalancerHost, var rebalancerHostNum) = await FindRebalancerHost(Silo1);
        var reportBeforeStop = await rebalancer.GetReport(cancellationToken);

        OutputHelper.WriteLine($"Now stopping Silo{rebalancerHostNum}, which is the host of the rebalancer\n");

        Assert.Equal(targetHost, rebalancerHost);
        Assert.NotEqual(rebalancerHost, Cluster.Silos[0].SiloAddress);

        await Cluster.StopSiloAsync(
            Cluster.Silos.First(x => x.SiloAddress.Equals(rebalancerHost)),
            cancellationToken);

        var reportAfterStop = await rebalancer.GetReport(cancellationToken);
        var newHost = reportAfterStop.Host;
        Assert.NotEqual(rebalancerHost, newHost);
        Assert.Equal(reportBeforeStop.ClusterImbalance, reportAfterStop.ClusterImbalance);
        Assert.Equal(reportBeforeStop.Status, reportAfterStop.Status);
        Assert.Equal(
            reportBeforeStop.Statistics
                .Single(statistic => statistic.SiloAddress.Equals(newHost))
                .AcquiredActivations,
            reportAfterStop.Statistics
                .Single(statistic => statistic.SiloAddress.Equals(newHost))
                .AcquiredActivations);
        Assert.Equal(
            reportBeforeStop.Statistics
                .Where(statistic => !statistic.SiloAddress.Equals(rebalancerHost))
                .OrderBy(statistic => statistic.SiloAddress.ToString(), StringComparer.Ordinal)
                .Select(statistic => (
                    statistic.TimeStamp,
                    statistic.SiloAddress,
                    statistic.DispersedActivations,
                    statistic.AcquiredActivations)),
            reportAfterStop.Statistics
                .Where(statistic => !statistic.SiloAddress.Equals(rebalancerHost))
                .OrderBy(statistic => statistic.SiloAddress.ToString(), StringComparer.Ordinal)
                .Select(statistic => (
                    statistic.TimeStamp,
                    statistic.SiloAddress,
                    statistic.DispersedActivations,
                    statistic.AcquiredActivations)));

        rebalancerEvents.Clear();
        await rebalancerEvents.WaitForCycleCountAsync(newHost, 3, WaitTimeout);

        stats = await MgmtGrain.GetDetailedGrainStatistics(null, null, cancellationToken);
        Assert.DoesNotContain(stats, statistic => statistic.SiloAddress.Equals(rebalancerHost));

        var silo1Activations = GetActivationCount(stats, Silo1);
        var silo2Activations = GetActivationCount(stats, Silo2);
        var silo3Activations = GetActivationCount(stats, Silo3);
        var silo4Activations = GetActivationCount(stats, Silo4);

        OutputHelper.WriteLine(
            $"Post-rebalancing activations:\n" +
            $"Silo1: {(rebalancerHostNum == 1 ? "DEAD" : silo1Activations)}\n" +
            $"Silo2: {(rebalancerHostNum == 2 ? "DEAD" : silo2Activations)}\n" +
            $"Silo3: {(rebalancerHostNum == 3 ? "DEAD" : silo3Activations)}\n" +
            $"Silo4: {(rebalancerHostNum == 4 ? "DEAD" : silo4Activations)}\n");

        (var finalHost, rebalancerHostNum) = await FindRebalancerHost(newHost);

        Assert.Equal(newHost, finalHost);
        OutputHelper.WriteLine($"The rebalancer is hosted by Silo{rebalancerHostNum} now");
    }

    private async Task MoveRebalancerToSilo(
        IActivationRebalancerWorker rebalancer,
        SiloAddress targetHost)
    {
        if ((await rebalancer.GetReport()).Host.Equals(targetHost))
        {
            return;
        }

        RequestContext.Set(IPlacementDirector.PlacementHintKey, targetHost);
        try
        {
            await rebalancer.Cast<IGrainManagementExtension>().MigrateOnIdle();
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }

        Assert.Equal(targetHost, (await rebalancer.GetReport()).Host);
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
