using Xunit;
using Xunit.Abstractions;

namespace UnitTests.ActivationRebalancingTests;

/// <summary>
/// Tests for static activation rebalancing without adding new activations during the process.
/// </summary>
[TestCategory("Functional"), TestCategory("ActivationRebalancing")]
public class StaticRebalancingTests(RebalancerFixture fixture, ITestOutputHelper output)
    : RebalancingTestBase<RebalancerFixture>(fixture, output), IClassFixture<RebalancerFixture>
{ 
    [Fact]
    public async Task Should_Move_Activations_From_Silo1_And_Silo3_To_Silo2_And_Silo4()
    {
        var tasks = new List<Task>();

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

        // Wait for 3 rebalancing cycles using event-driven waiting instead of Task.Delay
        // This is deterministic and faster than waiting 3x SessionCyclePeriod (9 seconds)
        const int targetCycles = 3;
        await RebalancerObserver.WaitForCycleCountAsync(targetCycles, timeout: TimeSpan.FromSeconds(30));

        stats = await MgmtGrain.GetDetailedGrainStatistics();
        var silo1Activations = GetActivationCount(stats, Silo1);
        var silo2Activations = GetActivationCount(stats, Silo2);
        var silo3Activations = GetActivationCount(stats, Silo3);
        var silo4Activations = GetActivationCount(stats, Silo4);

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

        var completedCycles = RebalancerObserver.GetCycleCount();
        OutputHelper.WriteLine(
            $"Post-rebalancing activations ({completedCycles} cycles):\n" +
            $"Silo1: {silo1Activations}\n" +
            $"Silo2: {silo2Activations}\n" +
            $"Silo3: {silo3Activations}\n" +
            $"Silo4: {silo4Activations}\n" +
            $"Variance: {postVariance} | Expected without rebalancing: {preVariance}");
    }
}