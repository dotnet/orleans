using Orleans.Runtime.Placement;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.ActivationRebalancingTests;

[TestCategory("Functional"), TestCategory("ActivationRebalancing")]
public class StaticRebalancingTests(RebalancingTestBase.Fixture fixture, ITestOutputHelper output)
    : RebalancingTestBase(fixture, output), IClassFixture<RebalancingTestBase.Fixture>
{ 
    [Fact]
    public async Task Should_Move_Activations_From_Silo1_And_Silo3_To_Silo2_And_Silo4()
    {
        await MgmtGrain.ForceActivationCollection(TimeSpan.Zero);

        var tasks = new List<Task>();

        RequestContext.Set(IPlacementDirector.PlacementHintKey, Silo1);
        for (var i = 0; i < 300; i++)
        {
            tasks.Add(GrainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }

        RequestContext.Set(IPlacementDirector.PlacementHintKey, Silo2);
        for (var i = 0; i < 30; i++)
        {
            tasks.Add(GrainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }

        RequestContext.Set(IPlacementDirector.PlacementHintKey, Silo3);
        for (var i = 0; i < 175; i++)
        {
            tasks.Add(GrainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }

        RequestContext.Set(IPlacementDirector.PlacementHintKey, Silo4);
        for (var i = 0; i < 100; i++)
        {
            tasks.Add(GrainFactory.GetGrain<IRebalancingTestGrain>(Guid.NewGuid()).Ping());
        }

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

        var index = 0;
        while (index < 5)
        {
            await Task.Delay(Fixture.SessionCyclePeriod);
            stats = await MgmtGrain.GetDetailedGrainStatistics();

            silo1Activations = GetActivationCount(stats, Silo1);
            silo2Activations = GetActivationCount(stats, Silo2);
            silo3Activations = GetActivationCount(stats, Silo3);
            silo4Activations = GetActivationCount(stats, Silo4);

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

        OutputHelper.WriteLine(
            $"Post-rebalancing activations ({index} cycles):\n" +
            $"Silo1: {silo1Activations}\n" +
            $"Silo2: {silo2Activations}\n" +
            $"Silo3: {silo3Activations}\n" +
            $"Silo4: {silo4Activations}\n");
    }
}