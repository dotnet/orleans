using System.Diagnostics.Metrics;
using System.Reflection;
using Orleans.Runtime;
using Orleans.Statistics;
using Xunit;

namespace UnitTests.Runtime;

public class EnvironmentStatisticsProviderTests
{
    [Fact, TestCategory("BVT"), TestCategory("Runtime")]
    public void RuntimeMemoryMetrics_AreObservableGauges()
    {
        using var provider = new EnvironmentStatisticsProvider();

        var availableMemoryMetric = GetInstrument(provider, "_availableMemoryCounter");
        var maximumAvailableMemoryMetric = GetInstrument(provider, "_maximumAvailableMemoryCounter");

        Assert.IsType<ObservableGauge<long>>(availableMemoryMetric);
        Assert.IsType<ObservableGauge<long>>(maximumAvailableMemoryMetric);
    }

    private static object GetInstrument(EnvironmentStatisticsProvider provider, string fieldName)
    {
        var field = typeof(EnvironmentStatisticsProvider).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var result = field.GetValue(provider);
        Assert.NotNull(result);

        return result;
    }
}
