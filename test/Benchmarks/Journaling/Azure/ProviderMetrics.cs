using System.Diagnostics.Metrics;

namespace Benchmarks.Journaling.Azure;

internal sealed record ProviderMetric(
    string Instrument,
    string Unit,
    string Provider,
    string Reason,
    long Observations,
    long Sum);

internal sealed class ProviderMetrics(Meter meter) : IDisposable
{
    private MeterListener? _listener;
    private readonly Dictionary<MetricKey, Aggregate> _values = [];
    private readonly object _lock = new();
    private bool _recording;

    public void Start()
    {
        lock (_lock)
        {
            if (_listener is not null)
            {
                throw new InvalidOperationException("Provider metric collection is already active.");
            }

            _values.Clear();
            _recording = true;
        }

        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, subscriber) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)
                && instrument is Counter<long>
                && instrument.Name is "orleans-journaling-provider-catalog-pages"
                    or "orleans-journaling-provider-catalog-items"
                    or "orleans-journaling-provider-catalog-entries"
                    or "orleans-journaling-provider-retries")
            {
                subscriber.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener = listener;
        listener.Start();
    }

    public IReadOnlyList<ProviderMetric> Stop()
    {
        Dispose();
        lock (_lock)
        {
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

    public void Dispose()
    {
        MeterListener? listener;
        lock (_lock)
        {
            _recording = false;
            listener = _listener;
            _listener = null;
        }

        listener?.Dispose();
    }

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
