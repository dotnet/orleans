using System.Diagnostics.Metrics;

namespace Orleans.Journaling;

internal sealed class JournalStorageTelemetry
{
    private static readonly Lazy<JournalStorageTelemetry> Direct = new(
        static () => new(new Meter("Microsoft.Orleans")));
    private readonly Counter<long> _catalogPages;
    private readonly Counter<long> _catalogItems;
    private readonly Counter<long> _catalogEntries;
    private readonly Counter<long> _retries;

    internal JournalStorageTelemetry(OrleansInstruments instruments) : this(instruments.Meter)
    {
    }

    private JournalStorageTelemetry(Meter meter)
    {
        _catalogPages = meter.CreateCounter<long>("orleans-journaling-provider-catalog-pages");
        _catalogItems = meter.CreateCounter<long>("orleans-journaling-provider-catalog-items");
        _catalogEntries = meter.CreateCounter<long>("orleans-journaling-provider-catalog-entries");
        _retries = meter.CreateCounter<long>("orleans-journaling-provider-retries");
    }

    internal static JournalStorageTelemetry CreateForDirectConstruction() => Direct.Value;

    internal void OnCatalogPage(string provider, long items)
    {
        if (_catalogPages.Enabled)
        {
            _catalogPages.Add(1, new KeyValuePair<string, object?>("provider", provider));
        }

        OnCatalogItems(provider, items);
    }

    internal void OnCatalogItems(string provider, long items)
    {
        if (_catalogItems.Enabled && items > 0)
        {
            _catalogItems.Add(items, new KeyValuePair<string, object?>("provider", provider));
        }
    }

    internal void OnCatalogEntry(string provider)
    {
        if (_catalogEntries.Enabled)
        {
            _catalogEntries.Add(1, new KeyValuePair<string, object?>("provider", provider));
        }
    }

    internal void OnRetry(string provider, string reason)
    {
        if (_retries.Enabled)
        {
            _retries.Add(1, new("provider", provider), new("reason", reason));
        }
    }
}
