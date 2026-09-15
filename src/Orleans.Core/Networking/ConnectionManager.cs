using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Internal;

namespace Orleans.Runtime.Messaging
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1001:Types that own disposable fields should be disposable",
        Justification = "The lifecycle adapter invokes the terminal asynchronous Close method, which disposes the cancellation source after all connections quiesce. A canceled host stop preserves it for remaining connection operations.")]
    internal sealed partial class ConnectionManager
    {
        [ThreadStatic]
        private static uint nextConnection;

        private readonly ConcurrentDictionary<SiloAddress, ConnectionEntry> connections = new();
        private readonly ConcurrentDictionary<Connection, Task> connectionTasks = new();
        private readonly AdmissionGate connectionEstablishment = new();
        private readonly ConnectionOptions connectionOptions;
        private readonly ConnectionFactory connectionFactory;
        private readonly ILogger logger;
        private readonly CancellationTokenSource shutdownCancellation = new();
#if NET9_0_OR_GREATER
        private readonly Lock lockObj = new();
#else
        private readonly object lockObj = new();
#endif
        private readonly TaskCompletionSource<int> closedTaskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConnectionManager(
            IOptions<ConnectionOptions> connectionOptions,
            ConnectionFactory connectionFactory,
            ILogger<ConnectionManager> logger)
        {
            this.connectionOptions = connectionOptions.Value;
            this.connectionFactory = connectionFactory;
            this.logger = logger;
        }

        public int ConnectionCount => connections.Sum(e => e.Value.Connections.Length);

        public Task Closed => this.closedTaskCompletionSource.Task;

        public List<SiloAddress> GetConnectedAddresses() => connections.Select(i => i.Key).ToList();

        public ValueTask<Connection> GetConnection(SiloAddress endpoint)
        {
            if (this.connections.TryGetValue(endpoint, out var entry) && entry.NextConnection() is { } connection)
            {
                if (!entry.HasSufficientConnections(connectionOptions) && entry.PendingConnection is null)
                {
                    this.GetConnectionAsync(endpoint).Ignore();
                }

                // Return the existing connection.
                return new(connection);
            }

            // Start a new connection attempt since there are no suitable connections.
            return new(this.GetConnectionAsync(endpoint));
        }
        public bool TryGetConnection(SiloAddress endpoint, [NotNullWhen(true)] out Connection? connection)
        {
            if (this.connections.TryGetValue(endpoint, out var entry) && entry.NextConnection() is { } c)
            {
                connection = c;
                return true;
            }

            connection = null;
            return false;
        }

        /// <summary>
        /// Gets the minimum elapsed time since any connection to the specified silo last received a message,
        /// or <see langword="null"/> if no connections exist or no messages have been received.
        /// </summary>
        /// <param name="endpoint">The silo address to check.</param>
        /// <returns>The elapsed time since the most recently received message across all connections, or <see langword="null"/>.</returns>
        public TimeSpan? GetElapsedSinceLastMessageReceived(SiloAddress endpoint)
        {
            if (!this.connections.TryGetValue(endpoint, out var entry))
            {
                return null;
            }

            TimeSpan? minElapsed = null;
            foreach (var connection in entry.Connections)
            {
                if (connection.ElapsedSinceLastMessageReceived is { } elapsed
                    && (minElapsed is null || elapsed < minElapsed))
                {
                    minElapsed = elapsed;
                }
            }

            return minElapsed;
        }

        private async Task<Connection> GetConnectionAsync(SiloAddress endpoint)
        {
            using var admission = this.connectionEstablishment.TryEnter();
            if (!admission.Entered)
            {
                throw new OperationCanceledException("Shutting down");
            }

            await Task.Yield();
            while (true)
            {
                if (this.shutdownCancellation.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Shutting down");
                }

                Task pendingAttempt;
                lock (this.lockObj)
                {
                    var entry = this.GetOrCreateEntry(endpoint);
                    entry.RemoveDefunct();

                    // If there are sufficient connections available then return an existing connection.
                    if (entry.HasSufficientConnections(connectionOptions) && entry.NextConnection() is { } connection)
                    {
                        return connection;
                    }

                    var remainingDelay = entry.GetRemainingRetryDelay(connectionOptions);
                    if (remainingDelay.Ticks > 0)
                    {
                        throw new ConnectionFailedException($"Unable to connect to {endpoint}, will retry after {remainingDelay.TotalMilliseconds}ms");
                    }

                    // If there is no pending attempt then start one, otherwise the pending attempt will be awaited before reevaluating.
                    pendingAttempt = entry.PendingConnection ??= ConnectAsync(endpoint, entry);
                }

                await pendingAttempt;
            }
        }

        private void OnConnectionFailed(ConnectionEntry entry)
        {
            var lastFailure = DateTime.UtcNow;
            lock (this.lockObj)
            {
                if (entry.LastFailure < lastFailure) entry.LastFailure = lastFailure;
                entry.PendingConnection = null;
                entry.RemoveDefunct();
            }
        }

        public void OnConnected(SiloAddress address, Connection connection)
        {
            // Outbound preambles run under the acquisition's existing admission until initialization completes.
            if (this.connectionTasks.ContainsKey(connection))
            {
                OnConnected(address, connection, null);
                return;
            }

            using var admission = this.connectionEstablishment.TryEnter();
            if (!admission.Entered)
            {
                throw new OperationCanceledException("Shutting down");
            }

            OnConnected(address, connection, null);
        }

        private void OnConnected(SiloAddress address, Connection connection, ConnectionEntry? entry)
        {
            lock (this.lockObj)
            {
                if (!connection.IsValid)
                {
                    throw new ConnectionAbortedException("Connection closed during initialization");
                }

                // The outbound attempt owns its pending task until initialization finishes.
                if (entry is not null)
                {
                    entry.PendingConnection = null;
                }

                entry ??= GetOrCreateEntry(address);
                entry.Connections = entry.Connections.Contains(connection) ? entry.Connections : entry.Connections.Add(connection);
                entry.LastFailure = default;
            }

            ConnectionEvents.EmitEstablished(connection, address);
            LogInformationConnectionEstablished(this.logger, connection, address);
        }

        public void OnConnectionTerminated(SiloAddress address, Connection connection, Exception? exception)
        {
            if (connection is null) return;
            ConnectionEvents.EmitTerminated(connection, exception);

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
                    else
                    {
                        entry.RemoveDefunct();
                    }
                }
            }

            if (exception != null && !this.shutdownCancellation.IsCancellationRequested)
            {
                LogWarningConnectionTerminated(this.logger, exception, connection);
            }
            else
            {
                LogDebugConnectionClosed(this.logger, connection);
            }
        }

        private ConnectionEntry GetOrCreateEntry(SiloAddress address) => connections.GetOrAdd(address, _ => new());

        private async Task<Connection> ConnectAsync(SiloAddress address, ConnectionEntry entry)
        {
            await Task.Yield();
            CancellationTokenSource? openConnectionCancellation = default;
            Connection? connection = default;
            Task? connectionTask = default;

            try
            {
                ConnectionEvents.EmitConnecting(address);
                LogInformationEstablishingConnection(this.logger, address);

                // Cancel pending connection attempts either when the host terminates or after the configured time limit.
                openConnectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(this.shutdownCancellation.Token, default);
                openConnectionCancellation.CancelAfter(this.connectionOptions.OpenConnectionTimeout);

                connection = await this.connectionFactory.ConnectAsync(address, openConnectionCancellation.Token);
                openConnectionCancellation.Token.ThrowIfCancellationRequested();

                ConnectionEvents.EmitConnected(address);
                LogInformationConnectedToEndpoint(this.logger, address);

                connectionTask = this.StartConnection(address, connection);

                await connection.Initialized.WaitAsync(openConnectionCancellation.Token);
                this.OnConnected(address, connection, entry);

                return connection;
            }
            catch (Exception exception)
            {
                if (connection is not null)
                {
                    try
                    {
                        await connection.CloseAsync(exception);
                    }
                    catch (Exception cleanupException)
                    {
                        LogWarningConnectionCleanupFailed(this.logger, cleanupException, address);
                    }

                    if (connectionTask is not null)
                    {
                        await connectionTask;
                    }
                }

                this.OnConnectionFailed(entry);

                LogWarningConnectionAttemptFailed(this.logger, exception, address);

                if (exception is OperationCanceledException && openConnectionCancellation?.IsCancellationRequested == true && !shutdownCancellation.IsCancellationRequested)
                    throw new ConnectionFailedException($"Connection attempt to endpoint {address} timed out after {connectionOptions.OpenConnectionTimeout}");

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
            ImmutableArray<Connection> connections;
            lock (this.lockObj)
            {
                if (!this.connections.TryGetValue(endpoint, out var entry))
                {
                    return;
                }

                connections = entry.Connections;
                if (entry.PendingConnection is null)
                {
                    this.connections.TryRemove(endpoint, out _);
                }
            }

            if (connections.Length == 1)
            {
                await connections[0].CloseAsync(exception: null);
            }
            else if (!connections.IsEmpty)
            {
                var closeTasks = new List<Task>();
                foreach (var connection in connections)
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

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1849:Call async methods when in an async method",
            Justification = "Shutdown callbacks must complete synchronously before connections are scanned, while aggregating all callback failures.")]
        public async Task Close(CancellationToken ct)
        {
            var establishmentDrained = this.connectionEstablishment.CloseAsync();
            var shutdownCompleted = false;
            try
            {
                LogDebugShuttingDownConnections(this.logger);

                this.shutdownCancellation.Cancel(throwOnFirstException: false);

                var cycles = 0;
                for (var closeTasks = new List<Task>(); ; closeTasks.Clear())
                {
                    var pendingEstablishment = !establishmentDrained.IsCompleted;
                    foreach (var kv in connections)
                    {
                        foreach (var connection in kv.Value.Connections)
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

                    // Runners remain owned through middleware cleanup, even after routing entries are removed.
                    foreach (var (connection, task) in this.connectionTasks)
                    {
                        closeTasks.Add(connection.CloseAsync(exception: null));
                        closeTasks.Add(task);
                    }

                    if (closeTasks.Count > 0 || pendingEstablishment)
                    {
                        // Signal existing connections before waiting for producers which may need those signals.
                        // The next scan also closes connections published while this drain was in progress.
                        closeTasks.Add(establishmentDrained);
                        await Task.WhenAll(closeTasks).WaitAsync(ct).SuppressThrowing();
                        if (ct.IsCancellationRequested)
                        {
                            shutdownCompleted = IsQuiescent();
                            break;
                        }
                    }
                    else
                    {
                        shutdownCompleted = true;
                        break;
                    }

                    await Task.Delay(10, ct).SuppressThrowing();
                    if (ct.IsCancellationRequested)
                    {
                        shutdownCompleted = IsQuiescent();
                        break;
                    }

                    if (++cycles > 100 && cycles % 500 == 0 && this.ConnectionCount is var remaining and > 0)
                    {
                        LogWarningWaitingForConnectionsToTerminate(this.logger, remaining);
                    }
                }
            }
            catch (Exception exception)
            {
                LogWarningExceptionDuringShutdown(this.logger, exception);
            }
            finally
            {
                if (shutdownCompleted)
                {
                    this.shutdownCancellation.Dispose();
                }

                this.closedTaskCompletionSource.TrySetResult(0);
            }

            bool IsQuiescent()
            {
                if (!establishmentDrained.IsCompleted || !this.connectionTasks.IsEmpty)
                {
                    return false;
                }

                foreach (var entry in connections.Values)
                {
                    if (entry.PendingConnection is not null || !entry.Connections.IsEmpty)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private Task StartConnection(SiloAddress address, Connection connection)
        {
            var started = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = started.Task.Unwrap();
            this.connectionTasks[connection] = completion;
            ThreadPool.UnsafeQueueUserWorkItem(state =>
            {
                var (manager, address, connection, started) = state;
                started.SetResult(manager.RunConnectionAsync(address, connection));
            }, (this, address, connection, started), preferLocal: false);
            return completion;
        }

        private async Task RunConnectionAsync(SiloAddress address, Connection connection)
        {
            Exception? error = default;
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
                this.connectionTasks.TryRemove(connection, out _);
            }
        }

        private IDisposable? BeginConnectionScope(Connection connection)
        {
            if (this.logger.IsEnabled(LogLevel.Critical))
            {
                return this.logger.BeginScope(new ConnectionLogScope(connection));
            }

            return null;
        }

        private sealed class ConnectionEntry
        {
            public Task? PendingConnection { get; set; }
            public DateTime LastFailure { get; set; }
            public ImmutableArray<Connection> Connections { get; set; } = ImmutableArray<Connection>.Empty;

            public TimeSpan GetRemainingRetryDelay(ConnectionOptions options)
            {
                var lastFailure = this.LastFailure;
                if (lastFailure.Ticks > 0)
                {
                    var retryAfter = lastFailure + options.ConnectionRetryDelay;
                    var remainingDelay = retryAfter - DateTime.UtcNow;
                    if (remainingDelay.Ticks > 0)
                    {
                        return remainingDelay;
                    }
                }

                return default;
            }

            public bool HasSufficientConnections(ConnectionOptions options) => Connections.Length >= options.ConnectionsPerEndpoint;

            public Connection? NextConnection()
            {
                var connections = this.Connections;
                if (connections.IsEmpty)
                {
                    return null;
                }

                var result = connections.Length == 1 ? connections[0] : connections[(int)(++nextConnection % (uint)connections.Length)];
                return result.IsValid ? result : null;
            }

            public void RemoveDefunct() => Connections = Connections.RemoveAll(c => !c.IsValid);
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Connection {Connection} established with {Silo}"
        )]
        private static partial void LogInformationConnectionEstablished(ILogger logger, Connection connection, SiloAddress silo);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Connection {Connection} terminated"
        )]
        private static partial void LogWarningConnectionTerminated(ILogger logger, Exception exception, Connection connection);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Connection {Connection} closed"
        )]
        private static partial void LogDebugConnectionClosed(ILogger logger, Connection connection);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Establishing connection to endpoint {EndPoint}"
        )]
        private static partial void LogInformationEstablishingConnection(ILogger logger, SiloAddress endPoint);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Connected to endpoint {EndPoint}"
        )]
        private static partial void LogInformationConnectedToEndpoint(ILogger logger, SiloAddress endPoint);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Connection attempt to endpoint {EndPoint} failed"
        )]
        private static partial void LogWarningConnectionAttemptFailed(ILogger logger, Exception exception, SiloAddress endPoint);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception cleaning up connection attempt to endpoint {EndPoint}"
        )]
        private static partial void LogWarningConnectionCleanupFailed(ILogger logger, Exception exception, SiloAddress endPoint);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Shutting down connections"
        )]
        private static partial void LogDebugShuttingDownConnections(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Waiting for {NumRemaining} connections to terminate"
        )]
        private static partial void LogWarningWaitingForConnectionsToTerminate(ILogger logger, int numRemaining);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "Exception during shutdown"
        )]
        private static partial void LogWarningExceptionDuringShutdown(ILogger logger, Exception exception);
    }
}
