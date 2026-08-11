using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class AzureTableJournalStorageInstrumentsTests
{
    [Fact]
    public void OnOperationCompleted_RecordsNamesTypesUnitsValuesAndOutcomeTags()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        using var serviceProvider = services.BuildServiceProvider();
        var meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        var orleansInstruments = new OrleansInstruments(meterFactory);
        var instruments = new AzureTableJournalStorageInstruments(orleansInstruments);
        using var operations = new MetricCollector<long>(
            orleansInstruments.Meter,
            "orleans-journaling-azure-table-operations");
        using var bytes = new MetricCollector<long>(
            orleansInstruments.Meter,
            "orleans-journaling-azure-table-operation-bytes");
        using var duration = new MetricCollector<double>(
            orleansInstruments.Meter,
            "orleans-journaling-azure-table-operation-duration");

        instruments.OnOperationCompleted(
            AzureTableJournalStorageInstruments.OperationAppend,
            TimeSpan.FromMilliseconds(8),
            bytes: 12,
            succeeded: true);
        instruments.OnOperationCompleted(
            AzureTableJournalStorageInstruments.OperationRead,
            TimeSpan.FromMilliseconds(-3),
            bytes: 99,
            succeeded: false);
        instruments.OnOperationCompleted(
            AzureTableJournalStorageInstruments.OperationDelete,
            TimeSpan.FromMilliseconds(5),
            bytes: 0,
            succeeded: true);

        Assert.Equal("orleans-journaling-azure-table-operations", operations.Instrument?.Name);
        Assert.IsType<Counter<long>>(operations.Instrument);
        Assert.Null(operations.Instrument.Unit);
        Assert.Equal("orleans-journaling-azure-table-operation-bytes", bytes.Instrument?.Name);
        Assert.IsType<Counter<long>>(bytes.Instrument);
        Assert.Equal("bytes", bytes.Instrument.Unit);
        Assert.Equal("orleans-journaling-azure-table-operation-duration", duration.Instrument?.Name);
        Assert.IsType<Histogram<double>>(duration.Instrument);
        Assert.Equal("ms", duration.Instrument.Unit);

        var operationMeasurements = operations.GetMeasurementSnapshot();
        Assert.Equal([1L, 1L, 1L], operationMeasurements.Select(static measurement => measurement.Value));
        Assert.True(operationMeasurements[0].MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "append"), new("status", "ok") }));
        Assert.True(operationMeasurements[1].MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "read"), new("status", "error") }));
        Assert.True(operationMeasurements[2].MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "delete"), new("status", "ok") }));

        var bytesMeasurement = Assert.Single(bytes.GetMeasurementSnapshot());
        Assert.Equal(12, bytesMeasurement.Value);
        Assert.True(bytesMeasurement.MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "append") }));

        var durationMeasurements = duration.GetMeasurementSnapshot();
        Assert.Equal([8d, 0d, 5d], durationMeasurements.Select(static measurement => measurement.Value));
        Assert.True(durationMeasurements[0].MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "append"), new("status", "ok") }));
        Assert.True(durationMeasurements[1].MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "read"), new("status", "error") }));
        Assert.True(durationMeasurements[2].MatchesTags(
            new KeyValuePair<string, object?>[] { new("operation", "delete"), new("status", "ok") }));
    }
}
