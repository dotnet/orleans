using System.Diagnostics.Metrics;

namespace Benchmarks.Journaling.Azure;

internal sealed record ProviderMetric(
    string Instrument,
    string Unit,
    string Provider,
    string Operation,
    string Api,
    string Status,
    string Reason,
    long Observations,
    double Sum,
    double? Mean,
    double? P50UpperBound,
    double? P95UpperBound,
    double? P99UpperBound);

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
                && instrument.Name.StartsWith("orleans-journaling-provider-", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
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
                .ThenBy(pair => pair.Key.Operation, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Api, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.Status, StringComparer.Ordinal)
                .Select(pair => pair.Value.Snapshot(pair.Key)).ToArray();
        }
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var key = new MetricKey(instrument.Name, instrument.Unit ?? "count", "", "", "", "", "");
        foreach (var tag in tags)
        {
            var text = tag.Value as string ?? "";
            key = tag.Key switch
            {
                "provider" => key with { Provider = text },
                "operation" => key with { Operation = text },
                "api" => key with { Api = text },
                "status" => key with { Status = text },
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
                _values.Add(key, aggregate = new Aggregate(instrument is Histogram<double>));
            }

            aggregate.Record(value);
        }
    }

    public void Dispose() => _listener.Dispose();

    private sealed record MetricKey(string Instrument, string Unit, string Provider, string Operation, string Api, string Status, string Reason);

    private sealed class Aggregate(bool histogram)
    {
        // Fixed memory, 10% bucket widths from 1 microsecond to more than an hour.
        private readonly long[] _buckets = new long[256];
        private long _count;
        private double _sum;

        public void Record(double value)
        {
            _count++;
            _sum += value;
            if (histogram)
            {
                var bucket = value <= .001 ? 0 : Math.Min(255, (int)Math.Ceiling(Math.Log(value / .001, 1.1)));
                _buckets[bucket]++;
            }
        }

        public ProviderMetric Snapshot(MetricKey key)
            => new(key.Instrument, key.Unit, key.Provider, key.Operation, key.Api, key.Status, key.Reason,
                _count, _sum, histogram ? _sum / _count : null, Percentile(.5), Percentile(.95), Percentile(.99));

        private double? Percentile(double quantile)
        {
            if (!histogram)
            {
                return null;
            }

            var target = (long)Math.Ceiling(_count * quantile);
            long seen = 0;
            for (var index = 0; index < _buckets.Length; index++)
            {
                seen += _buckets[index];
                if (seen >= target)
                {
                    return .001 * Math.Pow(1.1, index);
                }
            }

            throw new InvalidOperationException("Histogram count mismatch.");
        }
    }
}
