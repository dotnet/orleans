using Orleans.Placement.Rebalancing;
using TestExtensions;
using Xunit;

namespace UnitTests.ActivationRebalancingTests;

/// <summary>
/// Tests for static activation rebalancing without adding new activations during the process.
/// </summary>
[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("Placement")]
[TestCategory("Functional"), TestCategory("ActivationRebalancing")]
public class StaticRebalancingTests(RebalancerFixture fixture, ITestOutputHelper output)
    : RebalancingTestBase<RebalancerFixture>(fixture, output), IClassFixture<RebalancerFixture>
{
    private static readonly TimeSpan RebalancerSuspensionDuration = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Should_Move_Activations_From_Silo1_And_Silo3_To_Silo2_And_Silo4()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var tasks = new List<Task>();
        using var rebalancerEvents = RebalancerDiagnosticObserver.Create(Cluster);
        using var grainEvents = GrainDiagnosticObserver.Create(Cluster);
        var rebalancer = Cluster.Client!.GetGrain<IActivationRebalancerWorker>(0);
        var rebalancerHost = (await rebalancer.GetReport(cancellationToken)).Host;
        await rebalancer.SuspendRebalancing(RebalancerSuspensionDuration, cancellationToken);

        AddTestActivations(tasks, Silo1, 300);
        AddTestActivations(tasks, Silo2, 30);
        AddTestActivations(tasks, Silo3, 180);
        AddTestActivations(tasks, Silo4, 100);

        await Task.WhenAll(tasks);
        rebalancerEvents.Clear();

        var testGrainType = GrainFactory.GetGrain<IRebalancingTestGrain>(Guid.Empty).GetGrainId().Type;
        var stats = await MgmtGrain.GetDetailedGrainStatistics(null, null, cancellationToken);

        var initialSilo1Activations = GetActivationCount(stats, Silo1, testGrainType);
        var initialSilo2Activations = GetActivationCount(stats, Silo2, testGrainType);
        var initialSilo3Activations = GetActivationCount(stats, Silo3, testGrainType);
        var initialSilo4Activations = GetActivationCount(stats, Silo4, testGrainType);

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

        await MgmtGrain.ForceRuntimeStatisticsCollection([rebalancerHost], cancellationToken);
        grainEvents.Clear();
        var silo1Migration = grainEvents.WaitForAnyGrainDeactivatedAsync(
            deactivated =>
                deactivated.Reason.ReasonCode is DeactivationReasonCode.Migrating &&
                deactivated.GrainContext.GrainId.Type.Equals(testGrainType) &&
                Silo1.Equals(deactivated.GrainContext.Address.SiloAddress));
        var silo3Migration = grainEvents.WaitForAnyGrainDeactivatedAsync(
            deactivated =>
                deactivated.Reason.ReasonCode is DeactivationReasonCode.Migrating &&
                deactivated.GrainContext.GrainId.Type.Equals(testGrainType) &&
                Silo3.Equals(deactivated.GrainContext.Address.SiloAddress));

        const int observedCycles = 3;
        var observedRebalancing = rebalancerEvents.WaitForCycleCountAsync(rebalancerHost, observedCycles);
        await rebalancer.ResumeRebalancing(cancellationToken);
        await Task.WhenAll(observedRebalancing, silo1Migration, silo3Migration).WaitAsync(cancellationToken);

        stats = await MgmtGrain.GetDetailedGrainStatistics(null, null, cancellationToken);
        silo1Activations = GetActivationCount(stats, Silo1, testGrainType);
        silo2Activations = GetActivationCount(stats, Silo2, testGrainType);
        silo3Activations = GetActivationCount(stats, Silo3, testGrainType);
        silo4Activations = GetActivationCount(stats, Silo4, testGrainType);

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

        var preVariance = CalculateVariance([initialSilo1Activations, initialSilo2Activations, initialSilo3Activations, initialSilo4Activations]);
        var postVariance = CalculateVariance([silo1Activations, silo2Activations, silo3Activations, silo4Activations]);

        OutputHelper.WriteLine(
            $"Post-rebalancing activations ({observedCycles} cycles):\n" +
            $"Silo1: {silo1Activations}\n" +
            $"Silo2: {silo2Activations}\n" +
            $"Silo3: {silo3Activations}\n" +
            $"Silo4: {silo4Activations}\n" +
            $"Variance: {postVariance} | Expected without rebalancing: {preVariance}");
    }
}
