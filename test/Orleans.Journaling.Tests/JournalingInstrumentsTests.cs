using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public class JournalingInstrumentsTests
{
    [Fact]
    public void JournalingInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new JournalingInstruments(new OrleansInstruments(meterFactory));
        using var writeCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-journaling-state-write-requests");
        using var storageBytesCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-journaling-storage-bytes");
        using var compactionCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-journaling-compaction-triggers");

        instruments.OnStateWriteRequest(JournalingInstruments.OperationAppend, TimeSpan.FromMilliseconds(5), succeeded: true);
        instruments.OnStorageOperation(JournalingInstruments.OperationAppend, TimeSpan.FromMilliseconds(7), bytes: 9, succeeded: true);
        instruments.OnCompactionTriggered(JournalingInstruments.CompactionReasonUserSnapshot);

        Assert.Equal(1, Assert.Single(writeCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(9, Assert.Single(storageBytesCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(1, Assert.Single(compactionCollector.GetMeasurementSnapshot()).Value);
    }
}
