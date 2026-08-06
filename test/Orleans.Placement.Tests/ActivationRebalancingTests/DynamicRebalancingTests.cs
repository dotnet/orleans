using Xunit;
using Xunit.Abstractions;

namespace UnitTests.ActivationRebalancingTests;

/// <summary>
/// Tests for dynamic activation rebalancing while new activations are being created.
/// </summary>
[TestCategory("Functional"), TestCategory("ActivationRebalancing")]
public class DynamicRebalancingTests(RebalancerFixture fixture, ITestOutputHelper output)
    : RebalancingTestBase<RebalancerFixture>(fixture, output), IClassFixture<RebalancerFixture>
{
    [Fact]
    public async Task Should_Move_Activations_From_Silo1_And_Silo3_To_Silo2_And_Silo4_While_New_Activations_Are_Created()
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

        var silo1Activations = initialSilo1Activations;
        var silo2Activations = initialSilo2Activations;
        var silo3Activations = initialSilo3Activations;
        var silo4Activations = initialSilo4Activations;

        const int extraActivationsSilo1 = 30;
        const int extraActivationsSilo2 = 3;
        const int extraActivationsSilo3 = 18;
        const int extraActivationsSilo4 = 10;

        var index = 0;
        var extraRounds = 0;

        while (index < 5)
        {
            await Task.Delay(RebalancerFixture.SessionCyclePeriod);

            if (index % 2 == 0)
            {
                tasks.Clear();

                // add an extra 1/10 of the initial activation count for each silo
                AddTestActivations(tasks, Silo1, extraActivationsSilo1);
                AddTestActivations(tasks, Silo2, extraActivationsSilo2);
                AddTestActivations(tasks, Silo3, extraActivationsSilo3);
                AddTestActivations(tasks, Silo4, extraActivationsSilo4);

                await Task.WhenAll(tasks);

                OutputHelper.WriteLine(
                   $"Added extra activations on cycle {index + 1}:\n" +
                   $"Silo1: {extraActivationsSilo1}\n" +
                   $"Silo2: {extraActivationsSilo2}\n" +
                   $"Silo3: {extraActivationsSilo3}\n" +
                   $"Silo4: {extraActivationsSilo4}\n");

                extraRounds++;
            }

            stats = await MgmtGrain.GetDetailedGrainStatistics();

            silo1Activations = GetActivationCount(stats, Silo1);
            silo2Activations = GetActivationCount(stats, Silo2);
            silo3Activations = GetActivationCount(stats, Silo3);
            silo4Activations = GetActivationCount(stats, Silo4);

            index++;
        }

        var finalSilo1Activations = initialSilo1Activations + extraRounds * extraActivationsSilo1;
        var finalSilo2Activations = initialSilo2Activations + extraRounds * extraActivationsSilo2;
        var finalSilo3Activations = initialSilo3Activations + extraRounds * extraActivationsSilo3;
        var finalSilo4Activations = initialSilo4Activations + extraRounds * extraActivationsSilo4;

        Assert.True(silo1Activations < finalSilo1Activations,
            $"Did not expect Silo1 to have more activations than what it started + added afterwards: " +
            $"[{finalSilo1Activations} -> {silo1Activations}]");

        Assert.True(silo2Activations > finalSilo2Activations,
            $"Did not expect Silo2 to have less activations than what it started + added afterwards: " +
            $"[{finalSilo2Activations} -> {silo2Activations}]");

        Assert.True(silo3Activations < finalSilo3Activations,
            $"Did not expect Silo3 to have more activations than what it started + added afterwards: " +
            $"[{finalSilo3Activations} -> {silo3Activations}]");

        Assert.True(silo4Activations > finalSilo4Activations,
            "Did not expect Silo4 to have less activations than what it started + added afterwards: " +
            $"[{finalSilo4Activations} -> {silo4Activations}]");

        var preVariance = CalculateVariance([finalSilo1Activations, finalSilo2Activations, finalSilo3Activations, finalSilo4Activations]);
        var postVariance = CalculateVariance([silo1Activations, silo2Activations, silo3Activations, silo4Activations]);

        OutputHelper.WriteLine(
            $"Post-rebalancing activations ({index} cycles):\n" +
            $"Silo1: {silo1Activations} | Expected without rebalancing: {finalSilo1Activations}\n" +
            $"Silo2: {silo2Activations} | Expected without rebalancing: {finalSilo2Activations}\n" +
            $"Silo3: {silo3Activations} | Expected without rebalancing: {finalSilo3Activations}\n" +
            $"Silo4: {silo4Activations} | Expected without rebalancing: {finalSilo4Activations}\n" +
            $"Variance: {postVariance} | Expected without rebalancing: {preVariance}");
    }
}