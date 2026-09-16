using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Dissemination.IntegrationHarness;

internal sealed class Observation : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly object _lock = new();
    private readonly MeterListener _meters = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Queue<ApplyEvidence> _applies = [];
    private readonly object _connectionLock = new();
    private readonly Dictionary<ConnectionContext, ConnectionLifetime> _connections = [];
    private readonly HashSet<ConnectionLifetime> _drainingConnections = [];
    private Func<ConnectionContext, bool>? _blockedConnections;
    private int _partitioned;
    private long _broadcastsSent;
    private long _outgoingRepairs;

    public Observation()
    {
        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name.StartsWith("Microsoft.Orleans", StringComparison.Ordinal)
                && instrument.Name is "orleans-dissemination-broadcast-sent" or "orleans-dissemination-anti-entropy-exchanges")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.Start();
        _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
    }

    public bool Partitioned => Volatile.Read(ref _partitioned) != 0;
    public long BroadcastsSent => Interlocked.Read(ref _broadcastsSent);
    public long OutgoingRepairs => Interlocked.Read(ref _outgoingRepairs);

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

        try
        {
            await next(context);
        }
        finally
        {
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

    public ApplyEvidence[] Applies()
    {
        lock (_lock)
        {
            return _applies.ToArray();
        }
    }

    private void Record(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (instrument.Name == "orleans-dissemination-broadcast-sent")
        {
            Interlocked.Add(ref _broadcastsSent, value);
            return;
        }

        foreach (var tag in tags)
        {
            if (tag.Key == "direction" && Equals(tag.Value, "out"))
            {
                Interlocked.Add(ref _outgoingRepairs, value);
                return;
            }
        }
    }

    public void OnNext(DiagnosticListener value)
    {
        if (value.Name == "Microsoft.Orleans.Dissemination")
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
        lock (_lock)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }
        }
    }
}
