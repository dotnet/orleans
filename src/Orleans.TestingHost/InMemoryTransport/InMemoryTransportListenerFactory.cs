using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Internal;
using Orleans.Networking.Shared;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace Orleans.TestingHost.InMemoryTransport;

internal static class InMemoryTransportExtensions
{
    public static ISiloBuilder UseInMemoryConnectionTransport(this ISiloBuilder siloBuilder, InMemoryTransportConnectionHub hub)
    {
        siloBuilder.ConfigureServices(services =>
        {
            services.AddSingletonKeyedService<object, IConnectionFactory>(SiloConnectionFactory.ServicesKey, CreateInMemoryConnectionFactory(hub));
            services.AddSingletonKeyedService<object, IConnectionListenerFactory>(SiloConnectionListener.ServicesKey, CreateInMemoryConnectionListenerFactory(hub));
            services.AddSingletonKeyedService<object, IConnectionListenerFactory>(GatewayConnectionListener.ServicesKey, CreateInMemoryConnectionListenerFactory(hub));
        });

        return siloBuilder;
    }

    public static IClientBuilder UseInMemoryConnectionTransport(this IClientBuilder clientBuilder, InMemoryTransportConnectionHub hub)
    {
        clientBuilder.ConfigureServices(services =>
        {
            services.AddSingletonKeyedService<object, IConnectionFactory>(ClientOutboundConnectionFactory.ServicesKey, CreateInMemoryConnectionFactory(hub));
        });

        return clientBuilder;
    }

    private static Func<IServiceProvider, object, IConnectionFactory> CreateInMemoryConnectionFactory(InMemoryTransportConnectionHub hub)
    {
        return (IServiceProvider sp, object key) =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var sharedMemoryPool = sp.GetRequiredService<SharedMemoryPool>();
            return new InMemoryTransportConnectionFactory(hub, loggerFactory, sharedMemoryPool);
        };
    }

    private static Func<IServiceProvider, object, IConnectionListenerFactory> CreateInMemoryConnectionListenerFactory(InMemoryTransportConnectionHub hub)
    {
        return (IServiceProvider sp, object key) =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var sharedMemoryPool = sp.GetRequiredService<SharedMemoryPool>();
            return new InMemoryTransportListener(hub, loggerFactory, sharedMemoryPool);
        };
    }
}

internal class InMemoryTransportListener : IConnectionListenerFactory, IConnectionListener
{
    private readonly Channel<(InMemoryTransportConnection Connection, TaskCompletionSource<bool> ConnectionAcceptedTcs)> _acceptQueue = Channel.CreateUnbounded<(InMemoryTransportConnection, TaskCompletionSource<bool>)>();
    private readonly InMemoryTransportConnectionHub _hub;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SharedMemoryPool _memoryPool;
    private readonly CancellationTokenSource _disposedCts = new();

    public InMemoryTransportListener(InMemoryTransportConnectionHub hub, ILoggerFactory loggerFactory, SharedMemoryPool memoryPool)
    {
        _hub = hub;
        _loggerFactory = loggerFactory;
        _memoryPool = memoryPool;
    }

    public CancellationToken OnDisposed => _disposedCts.Token;

    public EndPoint EndPoint { get; set; }

    public async Task ConnectAsync(InMemoryTransportConnection connection)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_acceptQueue.Writer.TryWrite((connection, completion)))
        {
            var connected = await completion.Task;
            if (connected)
            {
                return;
            }
        }

        throw new ConnectionFailedException($"Unable to connect to {EndPoint} because its listener has terminated.");
    }

    public async ValueTask<ConnectionContext> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (await _acceptQueue.Reader.WaitToReadAsync(cancellationToken))
        {
            if (_acceptQueue.Reader.TryRead(out var item))
            {
                var remoteConnectionContext = item.Connection;
                var localConnectionContext = InMemoryTransportConnection.Create(
                    _memoryPool.Pool,
                    _loggerFactory.CreateLogger<InMemoryTransportConnection>(),
                    other: remoteConnectionContext,
                    localEndPoint: EndPoint);

                // Set the result to true to indicate that the connection was accepted.
                item.ConnectionAcceptedTcs.TrySetResult(true);

                return localConnectionContext;
            }
        }

        return null;
    }

    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        EndPoint = endpoint;
        _hub.RegisterConnectionListenerFactory(endpoint, this);
        return new ValueTask<IConnectionListener>(this);
    }

    public ValueTask DisposeAsync()
    {
        return UnbindAsync(default);
    }

    public ValueTask UnbindAsync(CancellationToken cancellationToken = default)
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
    private readonly ConcurrentDictionary<EndPoint, InMemoryTransportListener> _listeners = new();

    public static InMemoryTransportConnectionHub Instance { get; } = new();

    public void RegisterConnectionListenerFactory(EndPoint endPoint, InMemoryTransportListener listener)
    {
        _listeners[endPoint] = listener;
        listener.OnDisposed.Register(() =>
        {
            ((IDictionary<EndPoint, InMemoryTransportListener>)_listeners).Remove(new KeyValuePair<EndPoint, InMemoryTransportListener>(endPoint, listener));
        });
    }

    public InMemoryTransportListener GetConnectionListenerFactory(EndPoint endPoint)
    {
        _listeners.TryGetValue(endPoint, out var listener);
        return listener;
    }
}

internal class InMemoryTransportConnectionFactory : IConnectionFactory
{
    private readonly InMemoryTransportConnectionHub _hub;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SharedMemoryPool _memoryPool;
    private readonly IPEndPoint _localEndpoint;

    public InMemoryTransportConnectionFactory(InMemoryTransportConnectionHub hub, ILoggerFactory loggerFactory, SharedMemoryPool memoryPool)
    {
        _hub = hub;
        _loggerFactory = loggerFactory;
        _memoryPool = memoryPool;
        _localEndpoint = new IPEndPoint(IPAddress.Loopback, Random.Shared.Next(1024, ushort.MaxValue - 1024));
    }

    public async ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
    {
        var listener = _hub.GetConnectionListenerFactory(endpoint);
        if (listener is null)
        {
            throw new ConnectionFailedException($"Unable to connect to endpoint {endpoint} because no such endpoint is currently registered.");
        }

        var connectionContext = InMemoryTransportConnection.Create(
            _memoryPool.Pool,
            _loggerFactory.CreateLogger<InMemoryTransportConnection>(),
            _localEndpoint,
            endpoint);
        await listener.ConnectAsync(connectionContext).WithCancellation(cancellationToken);
        return connectionContext;
    }
}

