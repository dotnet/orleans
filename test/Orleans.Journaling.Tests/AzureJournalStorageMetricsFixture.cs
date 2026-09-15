using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Orleans.Journaling.Tests;

internal sealed class AzureJournalStorageMetricsFixture : IDisposable
{
    private readonly ServiceProvider _services;

    public AzureJournalStorageMetricsFixture(string provider)
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var instruments = new OrleansInstruments(_services.GetRequiredService<IMeterFactory>());
        if (provider == JournalStorageTelemetry.AzureBlob)
        {
            Blob = new(instruments, Clock);
        }
        else
        {
            Table = new(instruments, Clock);
        }

        Calls = new(instruments.Meter, "orleans-journaling-provider-api-calls");
        ApiDuration = new(instruments.Meter, "orleans-journaling-provider-api-call-duration");
        Items = new(instruments.Meter, "orleans-journaling-provider-api-items");
        Operations = new(instruments.Meter, "orleans-journaling-provider-operations");
        OperationDuration = new(instruments.Meter, "orleans-journaling-provider-operation-duration");
        Entries = new(instruments.Meter, "orleans-journaling-provider-catalog-entries");
        Retries = new(instruments.Meter, "orleans-journaling-provider-retries");
    }

    public FakeTimeProvider Clock { get; } = new();
    public AzureBlobJournalStorageInstruments? Blob { get; }
    public AzureTableJournalStorageInstruments? Table { get; }
    public MetricCollector<long> Calls { get; }
    public MetricCollector<double> ApiDuration { get; }
    public MetricCollector<long> Items { get; }
    public MetricCollector<long> Operations { get; }
    public MetricCollector<double> OperationDuration { get; }
    public MetricCollector<long> Entries { get; }
    public MetricCollector<long> Retries { get; }

    public void Dispose()
    {
        Calls.Dispose();
        ApiDuration.Dispose();
        Items.Dispose();
        Operations.Dispose();
        OperationDuration.Dispose();
        Entries.Dispose();
        Retries.Dispose();
        _services.Dispose();
    }
}
