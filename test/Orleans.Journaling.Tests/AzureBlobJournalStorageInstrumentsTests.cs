using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public class AzureBlobJournalStorageInstrumentsTests
{
    [Fact]
    public void AzureBlobJournalStorageInstruments_RecordsMetricsUsingMeterFactory()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var instruments = new AzureBlobJournalStorageInstruments(new OrleansInstruments(meterFactory));
        using var operationsCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-journaling-azure-blob-operations");
        using var bytesCollector = new MetricCollector<long>(meterFactory, "Microsoft.Orleans", "orleans-journaling-azure-blob-operation-bytes");
        using var durationCollector = new MetricCollector<double>(meterFactory, "Microsoft.Orleans", "orleans-journaling-azure-blob-operation-duration");

        instruments.OnOperationCompleted(AzureBlobJournalStorageInstruments.OperationAppend, TimeSpan.FromMilliseconds(8), bytes: 12, succeeded: true);

        Assert.Equal(1, Assert.Single(operationsCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(12, Assert.Single(bytesCollector.GetMeasurementSnapshot()).Value);
        Assert.Equal(8, Assert.Single(durationCollector.GetMeasurementSnapshot()).Value);
    }
}
