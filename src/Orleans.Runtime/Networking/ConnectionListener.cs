using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Messaging
{
    internal abstract class ConnectionListener
    {
        private readonly IConnectionListenerFactory listenerFactory;
        private readonly ConnectionManager connectionManager;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly ConcurrentDictionary<Connection, Task> connections = new ConcurrentDictionary<Connection, Task>(ReferenceEqualsComparer.Instance);
        private readonly INetworkingTrace trace;
        private IConnectionListener listener;
        private ConnectionDelegate connectionDelegate;
        private Task runTask;

        protected ConnectionListener(
            IServiceProvider serviceProvider,
            IConnectionListenerFactory listenerFactory,
            IOptions<ConnectionOptions> connectionOptions,
            ConnectionManager connectionManager,
            INetworkingTrace trace)
        {
            this.ServiceProvider = serviceProvider;
            this.listenerFactory = listenerFactory;
            this.connectionManager = connectionManager;
            this.ConnectionOptions = connectionOptions.Value;
            this.trace = trace;
        }

        public abstract EndPoint Endpoint { get; }

        protected IServiceProvider ServiceProvider { get; }

        public int ConnectionCount => this.connections.Count;

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
                    this.ConnectionOptions.ConfigureConnectionBuilder(connectionBuilder);
                    Connection.ConfigureBuilder(connectionBuilder);
                    return this.connectionDelegate = connectionBuilder.Build();
                }
            }
        }

        public async Task BindAsync(CancellationToken cancellationToken)
        {
            this.listener = await this.listenerFactory.BindAsync(this.Endpoint, cancellationToken);
        }

        public void Start()
        {
            if (this.runTask != null) throw new InvalidOperationException("Start has already been called");
            if (this.listener is null) throw new InvalidOperationException("Listener is not bound");
            this.runTask = Task.Run(() => this.RunAsync());
        }

        private async Task RunAsync()
        {
            var runCancellation = this.cancellation.Token;
            var runCancellationTask = runCancellation.WhenCancelled();
            while (!runCancellation.IsCancellationRequested)
            {
                try
                {
                    var acceptTask = this.listener.AcceptAsync(runCancellation);

                    if (!acceptTask.IsCompletedSuccessfully)
                    {
                        // Allow the call to be gracefully cancelled.
                        var completed = await Task.WhenAny(acceptTask.AsTask(), runCancellationTask);
                        if (ReferenceEquals(completed, runCancellationTask)) break;
                    }

                    var context = acceptTask.Result;
                    if (context == null) break;

                    var connection = this.CreateConnection(context);
                    _ = RunConnection(connection);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    this.trace.LogWarning("Exception in AcceptAsync: {Exception}", exception);
                }
            }

            async Task RunConnection(Connection connection)
            {
                try
                {
                    using (this.BeginConnectionScope(connection))
                    {
                        var connectionTask = connection.Run();
                        this.connections.TryAdd(connection, connectionTask);
                        await connectionTask;
                        this.trace.LogInformation("Connection {@Connection} terminated", connection);
                    }
                }
                catch (Exception exception)
                {
                    this.trace.LogInformation(exception, "Connection {@Connection} terminated with an exception", connection);
                }
                finally
                {
                    this.connections.TryRemove(connection, out _);
                }
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            this.cancellation.Cancel(throwOnFirstException: false);
            if (this.runTask != null)
            {
                try
                {
                    await this.runTask;
                }
                catch (Exception exception)
                {
                    this.trace.LogWarning(
                        exception,
                        "Exception while waiting for accept loop to terminate: {Exception}",
                        exception);
                }
            }
            
            try
            {
                ValueTask listenerStop = default;
                if (this.listener != null)
                {
                    listenerStop = this.listener.UnbindAsync(CancellationToken.None);
                }

                var cycles = 0;
                var exception = new ConnectionAbortedException("Shutting down");
                while (this.ConnectionCount > 0)
                {
                    foreach (var connection in this.connections.Keys.ToImmutableList())
                    {
                        try
                        {
                            connection.Close(exception);
                        }
                        catch
                        {
                        }
                    }

                    await Task.Delay(10);

                    if (cancellationToken.IsCancellationRequested) break;

                    if (++cycles > 100 && cycles % 500 == 0 && this.ConnectionCount > 0)
                    {
                        this.trace?.LogWarning("Waiting for {NumRemaining} connections to terminate", this.ConnectionCount);
                    }
                }

                await this.connectionManager.Closed;

                await listenerStop;
                if (this.listener != null)
                {
                    await this.listener.DisposeAsync();
                }
            }
            catch (Exception exception)
            {
                this.trace?.LogWarning("Exception during shutdown: {Exception}", exception);
            }
        }

        private IDisposable BeginConnectionScope(Connection connection)
        {
            if (this.trace.IsEnabled(LogLevel.Critical))
            {
                return this.trace.BeginScope(new ConnectionLogScope(connection));
            }

            return null;
        }
    }
}