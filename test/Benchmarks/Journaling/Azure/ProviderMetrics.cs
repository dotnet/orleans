using System.Diagnostics.Metrics;

namespace Benchmarks.Journaling.Azure;

internal sealed record ProviderMetric(
    string Instrument,
    string Unit,
    string Provider,
    string Reason,
    long Observations,
    long Sum);

internal sealed class ProviderMetrics : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<MetricKey, Aggregate> _values = [];
    private readonly object _lock = new();
    private bool _recording;

    public ProviderMetrics(Meter meter)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument is Counter<long>
                && instrument.Name is "orleans-journaling-provider-catalog-pages"
                    or "orleans-journaling-provider-catalog-items"
                    or "orleans-journaling-provider-catalog-entries"
                    or "orleans-journaling-provider-retries")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.Start();
    }

    public void Start()
    {
        lock (_lock)
        {
            _values.Clear();
            _recording = true;
        }
    }

    public IReadOnlyList<ProviderMetric> Stop()
    {
        lock (_lock)
        {
            _recording = false;
            return _values.OrderBy(pair => pair.Key.Instrument, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Provider, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Reason, StringComparer.Ordinal)
                .Select(pair => pair.Value.Snapshot(pair.Key)).ToArray();
        }
    }

    private void Record(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var key = new MetricKey(instrument.Name, instrument.Unit ?? "count", "", "");
        foreach (var tag in tags)
        {
            var text = tag.Value as string ?? "";
            key = tag.Key switch
            {
                "provider" => key with { Provider = text },
                "reason" => key with { Reason = text },
                _ => key
            };
        }

        lock (_lock)
        {
            if (!_recording)
            {
                return;
            }

            if (!_values.TryGetValue(key, out var aggregate))
            {
                _values.Add(key, aggregate = new Aggregate());
            }

            aggregate.Record(value);
        }
    }

    public void Dispose() => _listener.Dispose();

    private sealed record MetricKey(string Instrument, string Unit, string Provider, string Reason);

    private sealed class Aggregate
    {
        private long _count;
        private long _sum;

        public void Record(long value)
        {
            _count++;
            _sum += value;
        }

        public ProviderMetric Snapshot(MetricKey key)
            => new(key.Instrument, key.Unit, key.Provider, key.Reason, _count, _sum);
    }
}
