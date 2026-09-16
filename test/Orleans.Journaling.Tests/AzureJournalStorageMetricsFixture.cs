using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Orleans.Journaling.Tests;

internal sealed class AzureJournalStorageMetricsFixture : IDisposable
{
    private readonly ServiceProvider _services;

    public AzureJournalStorageMetricsFixture(string provider)
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var instruments = new OrleansInstruments(_services.GetRequiredService<IMeterFactory>());
        if (provider == nameof(AzureBlobJournalStorageProvider))
        {
            Blob = new(instruments);
        }
        else
        {
            Table = new(instruments);
        }

        Pages = new(instruments.Meter, "orleans-journaling-provider-catalog-pages");
        Items = new(instruments.Meter, "orleans-journaling-provider-catalog-items");
        Entries = new(instruments.Meter, "orleans-journaling-provider-catalog-entries");
        Retries = new(instruments.Meter, "orleans-journaling-provider-retries");
    }

    public AzureBlobJournalStorageInstruments? Blob { get; }
    public AzureTableJournalStorageInstruments? Table { get; }
    public MetricCollector<long> Pages { get; }
    public MetricCollector<long> Items { get; }
    public MetricCollector<long> Entries { get; }
    public MetricCollector<long> Retries { get; }

    public void Dispose()
    {
        Pages.Dispose();
        Items.Dispose();
        Entries.Dispose();
        Retries.Dispose();
        _services.Dispose();
    }
}
