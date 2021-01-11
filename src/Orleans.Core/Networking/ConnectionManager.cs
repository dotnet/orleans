using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;

namespace Orleans.Runtime.Messaging
{
    internal sealed class ConnectionManager
    {
        [ThreadStatic]
        private static int nextConnection;

        private readonly ConcurrentDictionary<SiloAddress, ConnectionEntry> connections = new ConcurrentDictionary<SiloAddress, ConnectionEntry>();
        private readonly ConnectionOptions connectionOptions;
        private readonly ConnectionFactory connectionFactory;
        private readonly NetworkingTrace trace;
        private readonly CancellationTokenSource shutdownCancellation = new CancellationTokenSource();
        private readonly object lockObj = new object();
        private readonly TaskCompletionSource<int> closedTaskCompletionSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConnectionManager(
            IOptions<ConnectionOptions> connectionOptions,
            ConnectionFactory connectionFactory,
            NetworkingTrace trace)
        {
            this.connectionOptions = connectionOptions.Value;
            this.connectionFactory = connectionFactory;
            this.trace = trace;
        }

        public int ConnectionCount
        {
            get
            {
                var count = 0;
                foreach (var entry in this.connections)
                {
                    var values = entry.Value.Connections;
                    if (values.IsDefault) continue;
                    count += values.Length;
                }

                return count;
            }
        }

        public Task Closed => this.closedTaskCompletionSource.Task;

        public ImmutableArray<SiloAddress> GetConnectedAddresses() => this.connections.Keys.ToImmutableArray();

        public ValueTask<Connection> GetConnection(SiloAddress endpoint)
        {
            if (this.connections.TryGetValue(endpoint, out var entry) && entry.TryGetConnection(out var connection))
            {
                var pendingAttempt = entry.PendingConnection;
                if (!entry.HasSufficientConnections && (pendingAttempt is null || pendingAttempt.IsCompleted))
                {
                    this.GetConnectionAsync(entry.Endpoint).Ignore();
                }

                // Return the existing connection.
                return new ValueTask<Connection>(connection);
            }

            // Start a new connection attempt since there are no suitable connections.
            return new ValueTask<Connection>(this.GetConnectionAsync(endpoint));
        }

        public bool TryGetConnection(SiloAddress endpoint, out Connection connection)
        {
            if (this.connections.TryGetValue(endpoint, out var entry))
            {
                return entry.TryGetConnection(out connection);
            }

            connection = null;
            return false;
        }

        public ImmutableArray<Connection> GetExistingConnections(SiloAddress endpoint)
        {
            if (this.connections.TryGetValue(endpoint, out var entry))
            {
                return entry.Connections;
            }

            return ImmutableArray<Connection>.Empty;
        }

        private async Task<Connection> GetConnectionAsync(SiloAddress endpoint)
        {
            while (true)
            {
                await Task.Yield();
                if (this.shutdownCancellation.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Shutting down");
                }

                ConnectionEntry entry;
                Task pendingAttempt;
                lock (this.lockObj)
                {
                    entry = this.GetOrCreateEntry(endpoint);

                    // Remove defunct connections.
                    foreach (var c in entry.Connections)
                    {
                        if (!c.IsValid) entry.Connections = entry.Connections.Remove(c);
                    }

                    // If there are sufficient connections available then return an existing connection.
                    if (entry.HasSufficientConnections && entry.TryGetConnection(out var connection))
                    {
                        return connection;
                    }

                    entry.ThrowIfRecentConnectionFailure();

                    pendingAttempt = entry.PendingConnection;

                    // If there is no pending attempt then start one, otherwise the pending attempt will be awaited before reevaluating.
                    if (pendingAttempt is null)
                    {
                        // Initiate a new connection.
                        pendingAttempt = entry.PendingConnection = this.ConnectAsync(endpoint);
                    }
                }

                try
                {
                    await pendingAttempt;
                }
                finally
                {
                    lock (this.lockObj)
                    {
                        // Clear the completed attempt.
                        if (ReferenceEquals(pendingAttempt, entry.PendingConnection))
                        {
                            entry.PendingConnection = null;
                        }
                    }
                }
            }
        }

        private void OnConnectionFailed(SiloAddress address, DateTime lastFailure)
        {
            lock (this.lockObj)
            {
                var entry = this.GetOrCreateEntry(address);
                if (entry.LastFailure.HasValue)
                {
                    var ticks = Math.Max(lastFailure.Ticks, entry.LastFailure.Value.Ticks);
                    lastFailure = new DateTime(ticks, DateTimeKind.Utc);
                }

                // Remove defunct connections.
                var connections = entry.Connections;
                foreach (var c in connections)
                {
                    if (!c.IsValid) connections = connections.Remove(c);
                }

                entry.LastFailure = lastFailure;
                entry.Connections = connections;
            }
        }

        public void OnConnected(SiloAddress address, Connection connection)
        {
            lock (this.lockObj)
            {
                var entry = this.GetOrCreateEntry(address);
                var newConnections = entry.Connections.Contains(connection) ? entry.Connections : entry.Connections.Add(connection);
                entry.LastFailure = default;
                entry.Connections = newConnections;
            }

            this.trace.LogInformation("Connection {Connection} established with {Silo}", connection, address);
        }

        public void OnConnectionTerminated(SiloAddress address, Connection connection, Exception exception)
        {
            if (connection is null) return;

            lock (this.lockObj)
            {
                if (this.connections.TryGetValue(address, out var entry))
                {
                    entry.Connections = entry.Connections.Remove(connection);

                    if (entry.Connections.Length == 0 && entry.PendingConnection is null)
                    {
                        // Remove the entire entry.
                        this.connections.TryRemove(address, out _);
                    }

                    foreach (var c in entry.Connections)
                    {
                        if (!c.IsValid) entry.Connections = entry.Connections.Remove(c);
                    }
                }
            }

            if (exception != null && !this.shutdownCancellation.IsCancellationRequested)
            {
                this.trace.LogWarning(
                    exception,
                    "Connection {Connection} terminated",
                    connection);
            }
            else
            {
                this.trace.LogDebug(
                    "Connection {Connection} closed",
                    connection);
            }
        }

        private ConnectionEntry GetOrCreateEntry(SiloAddress address)
        {
            lock (this.lockObj)
            {
                if (!this.connections.TryGetValue(address, out var entry))
                {
                    // Initialize the entry for this endpoint.
                    entry = new ConnectionEntry(this.connectionOptions, address, ImmutableArray<Connection>.Empty, default);
                    this.connections[address] = entry;
                }

                return entry;
            }
        }

        private async Task<Connection> ConnectAsync(SiloAddress address)
        {
            await Task.Yield();
            CancellationTokenSource openConnectionCancellation = default;

            try
            {
                if (this.trace.IsEnabled(LogLevel.Information))
                {
                    this.trace.LogInformation(
                        "Establishing connection to endpoint {EndPoint}",
                        address);
                }

                // Cancel pending connection attempts either when the host terminates or after the configured time limit.
                openConnectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(this.shutdownCancellation.Token);
                openConnectionCancellation.CancelAfter(this.connectionOptions.OpenConnectionTimeout);

                var connection = await this.connectionFactory.ConnectAsync(address, openConnectionCancellation.Token)
                    .AsTask()
                    .WithCancellation(openConnectionCancellation.Token);

                if (this.trace.IsEnabled(LogLevel.Information))
                {
                    this.trace.LogInformation(
                        "Connected to endpoint {EndPoint}",
                        address);
                }

                this.StartConnection(address, connection);
                this.OnConnected(address, connection);
                return connection;
            }
            catch (Exception exception)
            {
                this.OnConnectionFailed(address, DateTime.UtcNow);

                this.trace.LogWarning(
                    exception,
                    "Connection attempt to endpoint {EndPoint} failed",
                    address);

                throw new ConnectionFailedException(
                    $"Unable to connect to endpoint {address}. See {nameof(exception.InnerException)}", exception);
            }
            finally
            {
                openConnectionCancellation?.Dispose();
            }
        }

        public async Task CloseAsync(SiloAddress endpoint)
        {
            ConnectionEntry entry;
            lock (this.lockObj)
            {
                if (!this.connections.TryGetValue(endpoint, out entry))
                {
                    return;
                }

                lock (this.lockObj)
                {
                    if (entry.PendingConnection is null)
                    {
                        this.connections.TryRemove(endpoint, out _);
                    }
                }
            }

            if (entry is ConnectionEntry && !entry.Connections.IsDefault)
            {
                var closeTasks = new List<Task>();
                foreach (var connection in entry.Connections)
                {
                    try
                    {
                        closeTasks.Add(connection.CloseAsync(exception: null));
                    }
                    catch
                    {
                    }
                }

                await Task.WhenAll(closeTasks);
            }
        }

        public async Task Close(CancellationToken ct)
        {
            try
            {
                if (this.trace.IsEnabled(LogLevel.Information))
                {
                    this.trace.LogInformation("Shutting down connections");
                }

                this.shutdownCancellation.Cancel(throwOnFirstException: false);

                var cycles = 0;
                while (this.ConnectionCount > 0)
                {
                    var closeTasks = new List<Task>();
                    foreach (var entry in this.connections.Values.ToImmutableList())
                    {
                        if (entry.Connections.IsDefaultOrEmpty) continue;
                        foreach (var connection in entry.Connections)
                        {
                            try
                            {
                                closeTasks.Add(connection.CloseAsync(exception: null));
                            }
                            catch
                            {
                            }
                        }
                    }

                    await Task.WhenAny(Task.WhenAll(closeTasks), ct.WhenCancelled());
                    if (ct.IsCancellationRequested) break;

                    await Task.Delay(10);
                    if (++cycles > 100 && cycles % 500 == 0 && this.ConnectionCount > 0)
                    {
                        this.trace?.LogWarning("Waiting for {NumRemaining} connections to terminate", this.ConnectionCount);
                    }
                }
            }
            catch (Exception exception)
            {
                this.trace?.LogWarning(exception, "Exception during shutdown");
            }
            finally
            {
                this.closedTaskCompletionSource.TrySetResult(0);
            }
        }

        private void StartConnection(SiloAddress address, Connection connection)
        {
            ThreadPool.UnsafeQueueUserWorkItem(this.StartConnectionCore, (address, connection));
        }

        private void StartConnectionCore(object state)
        {
            var (address, connection) = ((SiloAddress, Connection))state;
            _ = this.RunConnectionAsync(address, connection);
        }

        private async Task RunConnectionAsync(SiloAddress address, Connection connection)
        {
            Exception error = default;
            try
            {
                using (this.BeginConnectionScope(connection))
                {
                    await connection.Run();
                }
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                this.OnConnectionTerminated(address, connection, error);
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

        private class ConnectionEntry
        {
            private readonly ConnectionOptions connectionOptions;

            public ConnectionEntry(
                ConnectionOptions connectionOptions,
                SiloAddress endpoint,
                ImmutableArray<Connection> connections,
                DateTime? lastFailure)
            {
                this.connectionOptions = connectionOptions;
                this.Endpoint = endpoint;
                this.Connections = connections;
                this.LastFailure = lastFailure;
            }

            public Task PendingConnection { get; set; }
            public DateTime? LastFailure { get; set; }
            public ImmutableArray<Connection> Connections { get; set; }
            public SiloAddress Endpoint { get; }

            public TimeSpan RemainingRetryDelay
            {
                get
                {
                    var lastFailure = this.LastFailure;
                    if (lastFailure.HasValue)
                    {
                        var now = DateTime.UtcNow;
                        var retryAfter = lastFailure.Value.Add(this.connectionOptions.ConnectionRetryDelay);
                        var remainingDelay = retryAfter.Subtract(now);
                        if (remainingDelay > TimeSpan.Zero)
                        {
                            return remainingDelay;
                        }
                    }

                    return TimeSpan.Zero;
                }
            }

            public bool HasSufficientConnections
            {
                get
                {
                    var connections = this.Connections;
                    return !connections.IsDefaultOrEmpty && connections.Length >= this.connectionOptions.ConnectionsPerEndpoint;
                }
            }

            public bool TryGetConnection(out Connection connection)
            {
                connection = default;
                var connections = this.Connections;
                if (connections.IsDefaultOrEmpty)
                {
                    return false;
                }

                nextConnection = (nextConnection + 1) % connections.Length;
                var result = connections[nextConnection];

                if (result.IsValid)
                {
                    connection = result;
                    return true;
                }

                return false;
            }

            public void ThrowIfRecentConnectionFailure()
            {
                var remainingDelay = this.RemainingRetryDelay;
                if (remainingDelay > TimeSpan.Zero)
                {
                    throw new ConnectionFailedException($"Unable to connect to {this.Endpoint}, will retry after {remainingDelay.TotalMilliseconds}ms");
                }
            }
        }
    }
}
