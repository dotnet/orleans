using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Support;

public sealed class DurableMessagingMetricProbe : IDisposable
{
    private readonly ConcurrentDictionary<(string Instrument, string TagValue), long> _measurements = [];
    private readonly ConcurrentDictionary<string, long> _gauges = [];
    private readonly object _lock = new();
    private TaskCompletionSource _changed = CreateSignal();
    private readonly MeterListener _listener;

    public DurableMessagingMetricProbe()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Microsoft.Orleans"
                    && instrument.Name is "orleans-durable-messaging-orphaned-jobs-reclaimed"
                        or "orleans-durable-messaging-inbox-messages-processed"
                        or "orleans-durablejobs-job-attempts-started"
                        or "orleans-durablejobs-handler-executions-started"
                        or "orleans-durablejobs-jobs-completed"
                        or "orleans-durablejobs-jobs-rescheduled"
                        or "orleans-durable-messaging-inbox-depth"
                        or "orleans-durable-messaging-outbox-depth")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.Start();
    }

    public long GetCount(string instrument, string tagValue = "") =>
        _measurements.TryGetValue((instrument, tagValue), out var count) ? count : 0;

    public long GetDepth(string instrument)
    {
        _listener.RecordObservableInstruments();
        return _gauges.TryGetValue(instrument, out var value) ? value : 0;
    }

    public Task WaitForCountAsync(
        string instrument,
        long expected,
        string tagValue = "") =>
        WaitForCountWithCancellationAsync(
            instrument,
            expected,
            tagValue,
            TestContext.Current.CancellationToken);

    public async Task WaitForCountWithCancellationAsync(
        string instrument,
        long expected,
        string tagValue,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (GetCount(instrument, tagValue) < expected)
        {
            Task changed;
            lock (_lock)
            {
                if (GetCount(instrument, tagValue) >= expected)
                {
                    return;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(timeout.Token);
        }
    }

    public void Dispose() => _listener.Dispose();

    private void OnMeasurement(
        Instrument instrument,
        long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        if (instrument.Name is "orleans-durable-messaging-inbox-depth" or "orleans-durable-messaging-outbox-depth")
        {
            _gauges[instrument.Name] = measurement;
            return;
        }

        var tagValue = "";
        var tagName = instrument.Name == "orleans-durable-messaging-inbox-messages-processed" ? "status" : "job_name";
        foreach (var tag in tags)
        {
            if (tag.Key == tagName)
            {
                tagValue = tag.Value as string ?? "";
                break;
            }
        }

        _measurements.AddOrUpdate(
            (instrument.Name, tagValue),
            measurement,
            (_, current) => current + measurement);
        lock (_lock)
        {
            _changed.TrySetResult();
            _changed = CreateSignal();
        }
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
