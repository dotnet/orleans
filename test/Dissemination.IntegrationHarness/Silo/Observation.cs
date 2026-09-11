using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Dissemination.IntegrationHarness;

internal sealed class Observation : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly object _lock = new();
    private readonly MeterListener _meters = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Dictionary<string, MetricValue> _metrics = [];
    private readonly Queue<ApplyEvidence> _applies = [];
    private readonly SocketCounters _sockets = new();
    private readonly object _connectionLock = new();
    private readonly Dictionary<ConnectionContext, ConnectionLifetime> _connections = [];
    private readonly HashSet<ConnectionLifetime> _drainingConnections = [];
    private Func<ConnectionContext, bool>? _blockedConnections;
    private int _partitioned;
    private long _bytesWritten;
    private long _bytesRead;

    private readonly bool _captureProvenance;

    public Observation(bool captureProvenance)
    {
        _captureProvenance = captureProvenance;
        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name.StartsWith("Microsoft.Orleans", StringComparison.Ordinal)
                && (instrument.Name is "orleans-messaging-sent-messages-size" or "orleans-messaging-received-messages-size"
                    || instrument.Name.StartsWith("orleans-dissemination-", StringComparison.Ordinal)))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.Start();
        _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
    }

    public bool Partitioned => Volatile.Read(ref _partitioned) != 0;
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    public (double Sent, double Received, long Samples) SocketSnapshot => _sockets.Snapshot();

    public void Partition()
    {
        ConnectionLifetime[] connections;
        lock (_connectionLock)
        {
            Volatile.Write(ref _partitioned, 1);
            connections = CaptureConnectionsUnsafe(static _ => true);
        }

        foreach (var connection in connections)
        {
            connection.Context.Abort(new ConnectionAbortedException("Harness partition"));
        }
    }

    public async Task DrainConnections(
        Func<ConnectionContext, bool> matches,
        Func<ConnectionContext, Task> close,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectionLifetime[] connections;
        lock (_connectionLock)
        {
            _blockedConnections = matches;
            connections = CaptureConnectionsUnsafe(matches);
        }

        var closing = new List<Task>(connections.Length * 2);
        foreach (var connection in connections)
        {
            if (!connection.Completion.Task.IsCompleted)
            {
                connection.Context.Abort(new ConnectionAbortedException("Harness partition"));
            }

            var closeTask = close(connection.Context);
            lock (_connectionLock)
            {
                connection.CloseTask = closeTask;
            }

            closing.Add(closeTask);
            closing.Add(connection.Completion.Task);
        }

        var drained = Task.WhenAll(closing);
        _ = drained.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await drained.WaitAsync(cancellationToken);
    }

    public void ResumeConnections()
    {
        lock (_connectionLock)
        {
            if (_drainingConnections.Any(static connection =>
                !connection.Completion.Task.IsCompleted || connection.CloseTask is not { IsCompletedSuccessfully: true }))
            {
                throw new InvalidOperationException("Partitioned connection middleware must drain before reopening.");
            }

            _drainingConnections.Clear();
            _blockedConnections = null;
            Volatile.Write(ref _partitioned, 0);
        }
    }

    private ConnectionLifetime[] CaptureConnectionsUnsafe(Func<ConnectionContext, bool> matches)
    {
        foreach (var connection in _connections.Values)
        {
            if (matches(connection.Context))
            {
                _drainingConnections.Add(connection);
            }
        }

        return [.. _drainingConnections];
    }

    public async Task Connection(ConnectionContext context, ConnectionDelegate next)
    {
        ConnectionLifetime? lifetime = null;
        lock (_connectionLock)
        {
            if (!Partitioned && _blockedConnections?.Invoke(context) != true)
            {
                lifetime = new(context);
                _connections.Add(context, lifetime);
            }
        }

        if (lifetime is null)
        {
            context.Abort(new ConnectionAbortedException("Harness partition"));
            return;
        }

        var original = context.Transport;
        context.Transport = new CountingDuplexPipe(original, this);
        try
        {
            await next(context);
        }
        finally
        {
            context.Transport = original;
            lock (_connectionLock)
            {
                _connections.Remove(context);
                lifetime.Completion.SetResult();
            }
        }
    }

    private sealed class ConnectionLifetime(ConnectionContext context)
    {
        public ConnectionContext Context { get; } = context;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? CloseTask { get; set; }
    }

    public Dictionary<string, MetricValue> Metrics()
    {
        _meters.RecordObservableInstruments();
        lock (_lock)
        {
            return new(_metrics);
        }
    }

    public ApplyEvidence[] Applies()
    {
        lock (_lock)
        {
            return _applies.ToArray();
        }
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var tagValues = new string[tags.Length];
        for (var index = 0; index < tags.Length; index++)
        {
            tagValues[index] = $"{tags[index].Key}={tags[index].Value}";
        }

        Array.Sort(tagValues, StringComparer.Ordinal);
        var key = $"{instrument.Name}|{string.Join(';', tagValues)}";
        lock (_lock)
        {
            var previous = _metrics.GetValueOrDefault(key, new(0, 0));
            _metrics[key] = instrument.IsObservable
                ? new(previous.Count + 1, value)
                : new(previous.Count + 1, previous.Sum + value);
        }
    }

    public void OnNext(DiagnosticListener value)
    {
        if (_captureProvenance && value.Name == "Microsoft.Orleans.Dissemination")
        {
            lock (_lock)
            {
                _subscriptions.Add(value.Subscribe(this));
            }
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key != "Dissemination.ValueApply" || value.Value is not { } payload)
        {
            return;
        }

        // Reflection keeps this observer identical in both binaries: the old runtime has no such type.
        object? Property(string name) => payload.GetType().GetProperty(name)?.GetValue(payload);
        var evidence = new ApplyEvidence(
            Property("Namespace")?.ToString() ?? "",
            Property("Key")?.ToString() ?? "",
            Convert.ToInt64(Property("FromVersion"), System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt64(Property("ToVersion"), System.Globalization.CultureInfo.InvariantCulture),
            Property("Result")?.ToString() ?? "",
            Property("Peer")?.ToString());
        lock (_lock)
        {
            _applies.Enqueue(evidence);
            while (_applies.Count > 4096)
            {
                _applies.Dequeue();
            }
        }
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }

    public void Dispose()
    {
        _meters.Dispose();
        _sockets.Dispose();
        lock (_lock)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }
        }
    }

    private sealed class CountingDuplexPipe(IDuplexPipe inner, Observation owner) : IDuplexPipe
    {
        public PipeReader Input { get; } = new CountingReader(inner.Input, owner);
        public PipeWriter Output { get; } = new CountingWriter(inner.Output, owner);
    }

    private sealed class CountingWriter(PipeWriter inner, Observation owner) : PipeWriter
    {
        public override void Advance(int bytes)
        {
            inner.Advance(bytes);
            Interlocked.Add(ref owner._bytesWritten, bytes);
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
    }

    private sealed class CountingReader(PipeReader inner, Observation owner) : PipeReader
    {
        private ReadOnlySequence<byte> _buffer;

        public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        {
            Interlocked.Add(ref owner._bytesRead, _buffer.Slice(0, consumed).Length);
            inner.AdvanceTo(consumed, examined);
            _buffer = default;
        }

        public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            var result = await inner.ReadAsync(cancellationToken);
            _buffer = result.Buffer;
            return result;
        }

        public override bool TryRead(out ReadResult result)
        {
            if (!inner.TryRead(out result))
            {
                return false;
            }

            _buffer = result.Buffer;
            return true;
        }

        public override void CancelPendingRead() => inner.CancelPendingRead();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
    }

    private sealed class SocketCounters : EventListener
    {
        private readonly object _lock = new();
        private double _sent;
        private double _received;
        private long _samples;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Net.Sockets")
            {
                EnableEvents(eventSource, EventLevel.LogAlways, EventKeywords.All,
                    new Dictionary<string, string?> { ["EventCounterIntervalSec"] = "1" });
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName != "EventCounters"
                || eventData.Payload?.Count != 1
                || eventData.Payload[0] is not IDictionary<string, object> counter
                || !counter.TryGetValue("Name", out var name))
            {
                return;
            }

            var incremental = counter.TryGetValue("Increment", out var measurement);
            if (!incremental && !counter.TryGetValue("Mean", out measurement))
            {
                return;
            }

            lock (_lock)
            {
                var value = Convert.ToDouble(measurement, System.Globalization.CultureInfo.InvariantCulture);
                if ((string)name == "bytes-sent")
                {
                    _sent = incremental ? _sent + value : value;
                    _samples++;
                }
                else if ((string)name == "bytes-received")
                {
                    _received = incremental ? _received + value : value;
                }
            }
        }

        public (double Sent, double Received, long Samples) Snapshot()
        {
            lock (_lock)
            {
                return (_sent, _received, _samples);
            }
        }
    }
}
