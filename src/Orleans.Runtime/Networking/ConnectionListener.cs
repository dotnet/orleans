#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Internal;
using Orleans.Connections.Transport;
using Orleans.Connections;
using Orleans.Runtime.Internal;

namespace Orleans.Runtime.Messaging;

internal abstract class ConnectionListener
{
    private readonly ConnectionManager _connectionManager;
    private readonly ConnectionCommon _connectionShared;
    private readonly MessageTransportListener[] _listeners;
    private readonly ConcurrentDictionary<Connection, object?> _connections = new(ReferenceEqualsComparer.Default);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private Task? _acceptLoopTask;

    protected ConnectionListener(
        IEnumerable<MessageTransportListener> listeners,
        IEnumerable<IMessageTransportListenerMiddleware> middleware,
        IOptions<ConnectionOptions> connectionOptions,
        ConnectionManager connectionManager,
        ConnectionCommon connectionShared)
    {

        // Get the listeners which are valid according to their configuration.
        _listeners = GetListeners(listeners, middleware).ToArray();
        _connectionManager = connectionManager;
        ConnectionOptions = connectionOptions.Value;
        _connectionShared = connectionShared;

        static IEnumerable<MessageTransportListener> GetListeners(IEnumerable<MessageTransportListener> registered, IEnumerable<IMessageTransportListenerMiddleware> middleware)
        {
            // Filter out duplicates and non-valid listeners
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var listener in registered)
            {
                if (!listener.IsValid) continue;
                if (!seen.Add(listener.ListenerName)) continue;
                var result = listener;

                foreach (var mw in middleware)
                {
                    result = mw.Apply(result);
                }

                yield return result;
            }
        }
    }

    protected bool HasListeners => _listeners is { Length: > 0 };

    protected IServiceProvider ServiceProvider => _connectionShared.ServiceProvider;

    protected ConnectionTrace TransportTrace => _connectionShared.ConnectionTrace;

    protected ConnectionOptions ConnectionOptions { get; }

    protected abstract Connection CreateConnection(MessageTransport transport);

    protected async Task BindAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<Task>(_listeners.Length);
        foreach (var listener in _listeners)
        {
            tasks.Add(listener.BindAsync(cancellationToken).AsTask());
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    protected void Start()
    {
        if (_listeners is { Length: 0 })
        {
            _acceptLoopTask = Task.CompletedTask;
            return;
        }

        using var _ = new ExecutionContextSuppressor();
        var tasks = new List<Task>(_listeners.Length);
        foreach (var listener in _listeners)
        {
            tasks.Add(RunAcceptLoop(listener));
        }

        _acceptLoopTask = Task.WhenAll(tasks);
    }

    private async Task RunAcceptLoop(MessageTransportListener listener)
    {
        await Task.Yield();
        try
        {
            while (true)
            {
                var context = await listener.AcceptAsync(_shutdownCancellation.Token).ConfigureAwait(false);
                if (context == null) break;

                var connection = CreateConnection(context);
                StartConnection(connection);
            }
        }
        catch (Exception exception)
        {
            TransportTrace.LogCritical(exception, $"Exception in AcceptAsync for listener {listener}");
        }
    }

    protected async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!HasListeners)
            {
                return;
            }

            await Task.WhenAll(_listeners.Select(listener => listener.UnbindAsync(cancellationToken).AsTask())).ConfigureAwait(false);
            _shutdownCancellation.Cancel();

            if (_acceptLoopTask is not null)
            {
                await _acceptLoopTask;
            }

            var closeTasks = new List<Task>();
            foreach (var kv in _connections)
            {
                closeTasks.Add(kv.Key.CloseAsync(exception: null));
            }

            if (closeTasks.Count > 0)
            {
                await Task.WhenAny(Task.WhenAll(closeTasks), cancellationToken.WhenCancelled());
            }

            await _connectionManager.Closed;
            await Task.WhenAll(_listeners.Select(listener => listener.DisposeAsync().AsTask()));
        }
        catch (Exception exception)
        {
            TransportTrace.LogWarning(exception, "Exception during shutdown");
        }
    }

    private void StartConnection(Connection connection)
    {
        _connections.TryAdd(connection, null);

        ThreadPool.UnsafeQueueUserWorkItem(state =>
        {
            var (t, connection) = ((ConnectionListener, Connection))state!;
            t.RunConnectionAsync(connection).Ignore();
        }, (this, connection));
    }

    private async Task RunConnectionAsync(Connection connection)
    {
        using (BeginConnectionScope(connection))
        {
            try
            {
                await connection.RunAsync();
                TransportTrace.LogInformation("Connection {Connection} terminated", connection);
            }
            catch (Exception exception)
            {
                TransportTrace.LogInformation(exception, "Connection {Connection} terminated with an exception", connection);
            }
            finally
            {
                _connections.TryRemove(connection, out _);
            }
        }
    }

    private IDisposable? BeginConnectionScope(Connection connection)
    {
        if (TransportTrace.IsEnabled(LogLevel.Critical))
        {
            return TransportTrace.BeginScope(new ConnectionLogScope(connection));
        }

        return null;
    }
}
