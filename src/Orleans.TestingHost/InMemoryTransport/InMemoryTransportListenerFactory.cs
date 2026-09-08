#nullable enable
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Connections.Transport;
using Orleans.Runtime.Messaging;

namespace Orleans.TestingHost.InMemoryTransport;

internal class InMemoryTransportListener : MessageTransportListener
{
    private readonly Channel<(InMemoryMessageTransport Connection, TaskCompletionSource<bool> ConnectionAcceptedTcs)> _acceptQueue = Channel.CreateUnbounded<(InMemoryMessageTransport, TaskCompletionSource<bool>)>();
    private readonly string? _endpointValue;
    private readonly InMemoryTransportConnectionHub _hub;
    private readonly CancellationTokenSource _disposedCts = new();

    public InMemoryTransportListener(string endpointName, string? endpointValue, InMemoryTransportConnectionHub hub)
    {
        ListenerName = endpointName;
        _endpointValue = endpointValue;
        _hub = hub;
    }

    public CancellationToken OnDisposed => _disposedCts.Token;

    public override bool IsValid => _endpointValue is not null && !OnDisposed.IsCancellationRequested;
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override string ListenerName { get; }

    public async Task AddConnection(InMemoryMessageTransport connection, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.UnsafeRegister(
            static state =>
            {
                var (source, token) = ((TaskCompletionSource<bool>, CancellationToken))state!;
                source.TrySetCanceled(token);
            },
            (completion, cancellationToken));

        if (_acceptQueue.Writer.TryWrite((connection, completion)))
        {
            var connected = await completion.Task;
            if (connected)
            {
                return;
            }
        }

        throw new ConnectionFailedException($"Unable to connect to endpoint because its listener has terminated.");
    }

    public override async ValueTask<MessageTransport?> AcceptAsync(CancellationToken cancellationToken = default)
    {
        while (await _acceptQueue.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_acceptQueue.Reader.TryRead(out var item))
            {
                // Acceptance and cancellation race through the same completion source.
                if (item.ConnectionAcceptedTcs.TrySetResult(true))
                {
                    return item.Connection;
                }
            }
        }

        return null;
    }

    public override ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        if (_endpointValue is not null)
        {
            _hub.RegisterConnectionListenerFactory(_endpointValue, this);
        }

        return default;
    }

    public override ValueTask DisposeAsync()
    {
        return UnbindAsync(default);
    }

    public override ValueTask UnbindAsync(CancellationToken cancellationToken = default)
    {
        _acceptQueue.Writer.TryComplete();
        while (_acceptQueue.Reader.TryRead(out var item))
        {
            // Set the result to false to indicate that the listener has terminated.
            item.ConnectionAcceptedTcs.TrySetResult(false);
        }

        _disposedCts.Cancel();
        return default;
    }
}

internal class InMemoryTransportConnectionHub
{
    private readonly ConcurrentDictionary<string, InMemoryTransportListener> _listeners = new();

    public static InMemoryTransportConnectionHub Instance { get; } = new();

    public void RegisterConnectionListenerFactory(string endpoint, InMemoryTransportListener listener)
    {
        _listeners[endpoint] = listener;
        listener.OnDisposed.Register(() =>
        {
            ((IDictionary<string, InMemoryTransportListener>)_listeners).Remove(new KeyValuePair<string, InMemoryTransportListener>(endpoint, listener));
        });
    }

    public async ValueTask<InMemoryTransportListener> GetConnectionListenerFactoryAsync(string endpoint, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_listeners.TryGetValue(endpoint, out var listener) && listener.IsValid)
            {
                return listener;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }
}

internal class InMemoryTransportConnector : MessageTransportConnector
{
    private readonly InMemoryTransportConnectionHub _hub;
    private readonly ILogger<InMemoryMessageTransport> _connectionLogger;

    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override bool IsValid => true;

    public InMemoryTransportConnector(InMemoryTransportConnectionHub hub, ILoggerFactory loggerFactory)
    {
        _hub = hub;
        _connectionLogger = loggerFactory.CreateLogger<InMemoryMessageTransport>();
    }

    public override async ValueTask<MessageTransport> CreateAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        var listener = await _hub.GetConnectionListenerFactoryAsync(endpoint.ToString()!, cancellationToken).ConfigureAwait(false);

        var pipePair = DuplexPipe.CreatePair();
        var local = new InMemoryMessageTransport(pipePair.Left, _connectionLogger);
        local.Start();
        var remote = new InMemoryMessageTransport(pipePair.Right, _connectionLogger);
        remote.Start();
        try
        {
            await listener.AddConnection(remote, cancellationToken);
            return local;
        }
        catch
        {
            await local.DisposeAsync();
            await remote.DisposeAsync();
            throw;
        }
    }

    private class DuplexPipe : IDuplexPipe
    {
        public required PipeReader Input { get; init; }
        public required PipeWriter Output { get; init; }

        public static (DuplexPipe Left, DuplexPipe Right) CreatePair()
        {
            var pipeOptions = new PipeOptions(readerScheduler: PipeScheduler.Inline, writerScheduler: PipeScheduler.Inline, useSynchronizationContext: false);
            var one = new Pipe(pipeOptions);
            var two = new Pipe(pipeOptions);
            var left = new DuplexPipe { Input = one.Reader, Output = two.Writer };
            var right = new DuplexPipe { Input = two.Reader, Output = one.Writer };
            return (left, right);
        }
    }
}
