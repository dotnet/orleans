using System.Collections;
using System.Net;
using System.Reflection;
using Orleans.Runtime;
using Orleans.Runtime.Placement.Rebalancing;
using Xunit;

namespace UnitTests.ActivationRebalancingTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Placement")]
[TestCategory("BVT")]
public sealed class RebalancerEntropyTests
{
    [Fact]
    public void EqualWeightedLoadsHaveUniformEntropy()
    {
        var result = ComputeEntropy([(100L, 10), (100L, 10)], 20, 100);

        Assert.Equal(Math.Log(2d), result, 14);
    }

    [Fact]
    public void ActivationWeightsProduceExpectedEntropy()
    {
        var result = ComputeEntropy([(100L, 10), (100L, 30)], 40, 100);
        var expected = -(0.25 * Math.Log(0.25) + 0.75 * Math.Log(0.75));

        Assert.Equal(expected, result, 14);
    }

    [Fact]
    public void MemoryWeightsProduceExpectedEntropy()
    {
        var result = ComputeEntropy([(100L, 10), (300L, 10)], 20, 100);
        var expected = -(0.25 * Math.Log(0.25) + 0.75 * Math.Log(0.75));

        Assert.Equal(expected, result, 14);
    }

    [Fact]
    public void EmptyLoadRetainsEpsilonContribution()
    {
        const double Epsilon = 1e-10;
        var result = ComputeEntropy([(100L, 10), (100L, 0)], 10, 100);

        Assert.Equal(-Epsilon * Math.Log(Epsilon), result, 14);
    }

    private static double ComputeEntropy((long MemoryUsage, int ActivationCount)[] values, int totalActivations, double meanMemory)
    {
        var statisticsType = typeof(ActivationRebalancerWorker).GetNestedType("ResourceStatistics", BindingFlags.NonPublic)!;
        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(SiloAddress), statisticsType);
        var dictionary = Assert.IsAssignableFrom<IDictionary>(Activator.CreateInstance(dictionaryType));
        for (var index = 0; index < values.Length; index++)
        {
            dictionary.Add(
                SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111 + index), 1),
                Activator.CreateInstance(statisticsType, values[index].MemoryUsage, values[index].ActivationCount));
        }

        var method = typeof(ActivationRebalancerWorker).GetMethod("ComputeEntropy", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, [dictionaryType.GetProperty("Values")!.GetValue(dictionary), totalActivations, meanMemory]);
        return Assert.IsType<double>(result);
    }
}
