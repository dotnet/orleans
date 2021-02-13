using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;

namespace Orleans.Runtime.Messaging
{
    internal abstract class ConnectionListener
    {
        private readonly IConnectionListenerFactory listenerFactory;
        private readonly ConnectionManager connectionManager;
        protected readonly ConcurrentDictionary<Connection, object> connections = new(ReferenceEqualsComparer.Instance);
        private readonly ConnectionCommon connectionShared;
        private TaskCompletionSource<object> acceptLoopTcs;
        private IConnectionListener listener;
        private ConnectionDelegate connectionDelegate;

        protected ConnectionListener(
            IConnectionListenerFactory listenerFactory,
            IOptions<ConnectionOptions> connectionOptions,
            ConnectionManager connectionManager,
            ConnectionCommon connectionShared)
        {
            this.listenerFactory = listenerFactory;
            this.connectionManager = connectionManager;
            this.ConnectionOptions = connectionOptions.Value;
            this.connectionShared = connectionShared;
        }

        public abstract EndPoint Endpoint { get; }

        protected IServiceProvider ServiceProvider => this.connectionShared.ServiceProvider;

        protected NetworkingTrace NetworkingTrace => this.connectionShared.NetworkingTrace;

        protected ConnectionOptions ConnectionOptions { get; }

        protected abstract Connection CreateConnection(ConnectionContext context);

        protected ConnectionDelegate ConnectionDelegate
        {
            get
            {
                if (this.connectionDelegate != null) return this.connectionDelegate;

                lock (this)
                {
                    if (this.connectionDelegate != null) return this.connectionDelegate;

                    // Configure the connection builder using the user-defined options.
                    var connectionBuilder = new ConnectionBuilder(this.ServiceProvider);
                    connectionBuilder.Use(next =>
                    {
                        return context =>
                        {
                            context.Features.Set<IUnderlyingTransportFeature>(new UnderlyingConnectionTransportFeature { Transport = context.Transport });
                            return next(context);
                        };
                    });
                    this.ConfigureConnectionBuilder(connectionBuilder);
                    Connection.ConfigureBuilder(connectionBuilder);
                    return this.connectionDelegate = connectionBuilder.Build();
                }
            }
        }
        protected virtual void ConfigureConnectionBuilder(IConnectionBuilder connectionBuilder) { }

        public async Task BindAsync(CancellationToken cancellationToken)
        {
            this.listener = await this.listenerFactory.BindAsync(this.Endpoint, cancellationToken);
        }

        public void Start()
        {
            if (this.listener is null) throw new InvalidOperationException("Listener is not bound");
            this.acceptLoopTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            ThreadPool.UnsafeQueueUserWorkItem(this.StartAcceptingConnections, this.acceptLoopTcs);
        }

        private void StartAcceptingConnections(object completionObj)
        {
            _ = RunAcceptLoop((TaskCompletionSource<object>)completionObj);

            async Task RunAcceptLoop(TaskCompletionSource<object> completion)
            {
                try
                {
                    while (true)
                    {
                        var context = await this.listener.AcceptAsync();
                        if (context == null) break;

                        var connection = this.CreateConnection(context);
                        this.StartConnection(connection);
                    }
                }
                catch (Exception exception)
                {
                    this.NetworkingTrace.LogCritical("Exception in AcceptAsync: {Exception}", exception);
                }
                finally
                {
                    completion.TrySetResult(null);
                }
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (this.acceptLoopTcs is object)
                {
                    await Task.WhenAll(
                        this.listener.UnbindAsync(cancellationToken).AsTask(),
                        this.acceptLoopTcs.Task);
                }
                else
                {
                    await this.listener.UnbindAsync(cancellationToken);
                }

                var cycles = 0;
                var cancellationTask = cancellationToken.WhenCancelled();
                for (var closeTasks = new List<Task>(); ; closeTasks.Clear())
                {
                    foreach (var kv in connections)
                    {
                        closeTasks.Add(kv.Key.CloseAsync(exception: null));
                    }

                    if (closeTasks.Count == 0) break;

                    await Task.WhenAny(Task.WhenAll(closeTasks), cancellationTask);

                    if (cancellationToken.IsCancellationRequested) break;

                    if (++cycles > 100 && cycles % 500 == 0 && connections.Count is var remaining and > 0)
                    {
                        this.NetworkingTrace.LogWarning("Waiting for {NumRemaining} connections to terminate", remaining);
                    }
                }

                await this.connectionManager.Closed;

                if (this.listener != null)
                {
                    await this.listener.DisposeAsync();
                }
            }
            catch (Exception exception)
            {
                this.NetworkingTrace.LogWarning("Exception during shutdown: {Exception}", exception);
            }
        }

        private void StartConnection(Connection connection)
        {
            connections.TryAdd(connection, null);

            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                var (t, connection) = ((ConnectionListener, Connection))state;
                _ = t.RunConnectionAsync(connection);
            }, (this, connection));
        }

        private async Task RunConnectionAsync(Connection connection)
        {
            using (this.BeginConnectionScope(connection))
            {
                try
                {
                    await connection.Run();
                    this.NetworkingTrace.LogInformation("Connection {Connection} terminated", connection);
                }
                catch (Exception exception)
                {
                    this.NetworkingTrace.LogInformation(exception, "Connection {Connection} terminated with an exception", connection);
                }
                finally
                {
                    this.connections.TryRemove(connection, out _);
                }
            }
        }

        private IDisposable BeginConnectionScope(Connection connection)
        {
            if (this.NetworkingTrace.IsEnabled(LogLevel.Critical))
            {
                return this.NetworkingTrace.BeginScope(new ConnectionLogScope(connection));
            }

            return null;
        }
    }
}