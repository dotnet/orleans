using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Orleans.Journaling.Ranges;
using StackExchange.Redis;

namespace Orleans.Journaling;

/// <summary>
/// Routes journal procedures to the partitions which own their buckets, according to a cached view of the range map.
/// </summary>
/// <remarks>
/// <para>
/// The router applies views in version order. A partition rejects an operation on a bucket it does not hold open with
/// <c>WrongPartition</c>, reporting the version of the bucket's last transition. If that version is newer than the
/// router's view, the router refreshes its view from the MetaStore and retries at once; otherwise the owner has not
/// yet opened the bucket, and the router retries with backoff. Unreachable partitions, lost replies, and transactions
/// which did not run are also retried with backoff, refreshing the view in case the partition's endpoint changed.
/// Every retry reuses the operation's arguments until <see cref="GarnetJournalStorageOptions.OperationTimeout"/>
/// elapses, which is safe because each mutation carries the ETag it writes, and a procedure which finds that ETag
/// among the journal's recent mutations reports the earlier attempt's success.
/// </para>
/// <para>
/// A partition which holds a bucket open but does not hold a journal answers <c>NeedPull</c> while the bucket's
/// migration is incomplete. The router then pulls the journal from the bucket's migration chain into the partition
/// (see <see cref="GarnetJournalMigrator.TryPullAsync"/>) and retries the operation.
/// </para>
/// <para>
/// Safety does not depend on the view: only a partition which holds a bucket open accepts operations on it, and at
/// most one partition holds a bucket open. A stale view or endpoint costs retries, never correctness.
/// </para>
/// </remarks>
internal sealed class GarnetJournalRouter : IAsyncDisposable
{
    internal const string ProviderName = "garnet";
    private const string TransactionFailedMessage = "ERR Transaction failed.";
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(1);

    private readonly IRangeMetaStore _metaStore;
    private readonly TimeSpan _operationTimeout;
    private readonly JournalStorageTelemetry _telemetry;
    private readonly ILogger? _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, PartitionConnection> _connections = new(StringComparer.Ordinal);
    private readonly List<Task> _retiring = [];
    private readonly CancellationTokenSource _shutdown = new();
    private RangeMapSnapshot _view;
    private Task<RangeMapSnapshot>? _refresh;
    private bool _stopped;

    /// <summary>
    /// Initializes a new instance of the <see cref="GarnetJournalRouter"/> class.
    /// </summary>
    /// <param name="metaStore">The store from which the router refreshes its view.</param>
    /// <param name="view">The initial view, which must be valid.</param>
    /// <param name="operationTimeout">How long an operation retries.</param>
    /// <param name="telemetry">The telemetry sink.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock which timestamps deletions.</param>
    public GarnetJournalRouter(
        IRangeMetaStore metaStore,
        RangeMapSnapshot view,
        TimeSpan operationTimeout,
        JournalStorageTelemetry? telemetry = null,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(metaStore);
        ArgumentNullException.ThrowIfNull(view);
        view.Map.Validate();
        _metaStore = metaStore;
        _view = view;
        _operationTimeout = operationTimeout;
        _telemetry = telemetry ?? JournalStorageTelemetry.CreateForDirectConstruction();
        _logger = logger;
        TimeProvider = timeProvider ?? TimeProvider.System;
        BucketCount = view.Map.BucketCount;
        MapId = view.Map.Id;
        Migrator = new GarnetJournalMigrator(this);
    }

    /// <summary>Gets the clock which timestamps deletions.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>Gets the client of the migration procedures, which pulls journals for this router.</summary>
    public GarnetJournalMigrator Migrator { get; }

    /// <summary>Gets how long an operation retries.</summary>
    public TimeSpan OperationTimeout => _operationTimeout;

    /// <summary>Gets the MetaStore from which the router refreshes its view.</summary>
    public IRangeMetaStore MetaStore => _metaStore;

    /// <summary>
    /// Gets or sets a fault injector for tests: when it returns <see langword="true"/> for a command which ran, the
    /// router discards the reply as if it were lost, and the caller retries.
    /// </summary>
    internal Func<string, bool>? DropReply { get; set; }

    /// <summary>
    /// Gets or sets a hook for tests, awaited before a command is sent to a specific partition, so a test can hold a
    /// migration step at a procedure boundary while it injects a fault.
    /// </summary>
    internal Func<string, CancellationToken, ValueTask>? BeforeSend { get; set; }

    /// <summary>Gets the number of buckets, which is the same in every view.</summary>
    public int BucketCount { get; }

    /// <summary>Gets the identity of the range map, which is the same in every view.</summary>
    public string MapId { get; }

    /// <summary>Gets the current view.</summary>
    public RangeMapSnapshot View => Volatile.Read(ref _view);

    /// <summary>
    /// Runs a procedure on the partition which owns <paramref name="bucket"/>.
    /// </summary>
    /// <param name="bucket">The bucket the procedure's keys belong to.</param>
    /// <param name="procedure">The procedure name.</param>
    /// <param name="args">The procedure's arguments, reused unchanged by every attempt.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="ambiguous">
    /// Set to <see langword="true"/> if an attempt failed in a way which may follow a commit: a connection failure, a
    /// lost reply, or the deadline passing after the command may have been sent. Attempts which the partition rejected
    /// (<c>WrongPartition</c>), transactions which did not run, and attempts never sent leave it unchanged.
    /// </param>
    /// <returns>The procedure's response, whose status is not <c>WrongPartition</c>.</returns>
    /// <exception cref="TimeoutException">The operation did not complete within the operation timeout.</exception>
    /// <exception cref="ObjectDisposedException">The router was disposed before the operation completed.</exception>
    public ValueTask<RedisResult[]> ExecuteAsync(int bucket, string procedure, object[] args, CancellationToken cancellationToken, StrongBox<bool>? ambiguous = null)
        => ExecuteCoreAsync(bucket, keys: null, procedure, args, ambiguous, cancellationToken);

    /// <summary>
    /// Runs a journal procedure on the partition which owns the journal's bucket, pulling the journal from the bucket's
    /// migration chain whenever the partition answers <c>NeedPull</c>.
    /// </summary>
    /// <param name="keys">The journal's keys.</param>
    /// <param name="procedure">The procedure.</param>
    /// <param name="args">The procedure's arguments, identical on every attempt.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="ambiguous">
    /// Set to <see langword="true"/> if an attempt failed in a way which may follow a commit, as for
    /// <see cref="ExecuteAsync"/>. A <c>NeedPull</c> reply leaves it unchanged: the procedure did not run.
    /// </param>
    /// <returns>The procedure's response, whose status is neither <c>WrongPartition</c> nor <c>NeedPull</c>.</returns>
    /// <exception cref="TimeoutException">The operation did not complete within the operation timeout.</exception>
    /// <exception cref="ObjectDisposedException">The router was disposed before the operation completed.</exception>
    public ValueTask<RedisResult[]> ExecuteJournalAsync(GarnetJournalKeySet keys, string procedure, object[] args, CancellationToken cancellationToken, StrongBox<bool>? ambiguous = null)
        => ExecuteCoreAsync(keys.Bucket, keys, procedure, args, ambiguous, cancellationToken);

    /// <summary>
    /// Runs a command once on a specific partition, at its endpoint in the current view.
    /// </summary>
    /// <returns>The command's response, or <see langword="null"/> if the view has no endpoint for the partition.</returns>
    /// <exception cref="RedisException">The partition was unreachable or the transaction did not run; see <see cref="IsTransient"/>.</exception>
    public async ValueTask<RedisResult[]?> TryExecuteAtAsync(PartitionId partition, string command, object[] args, CancellationToken cancellationToken)
    {
        if (GetConnection(View, partition) is not { } connection)
        {
            return null;
        }

        if (BeforeSend is { } beforeSend)
        {
            await beforeSend(command, cancellationToken).ConfigureAwait(false);
        }

        var database = await connection.GetDatabaseAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = await database.ExecuteAsync(command, args).WaitAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfReplyDropped(command);
        return result.Resp2Type == ResultType.Array ? (RedisResult[])result! : [result];
    }

    /// <summary>
    /// Runs a command on a specific partition, retrying while the partition is unavailable until the operation timeout.
    /// </summary>
    /// <returns>The command's response, whatever its status.</returns>
    /// <exception cref="TimeoutException">The partition was unavailable for the whole operation timeout.</exception>
    public async ValueTask<RedisResult[]> ExecuteAtAsync(PartitionId partition, string command, object[] args, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var backoff = InitialBackoff;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Exception? exception = null;
            try
            {
                if (await TryExecuteAtAsync(partition, command, args, cancellationToken).AsTask().WaitAsync(GetRemaining(start), cancellationToken).ConfigureAwait(false) is { } result)
                {
                    return result;
                }
            }
            catch (Exception ex) when (IsTransient(ex) || ex is OperationCanceledException && !IsStopped && !cancellationToken.IsCancellationRequested)
            {
                // Closing a connection retired by a view change cancels the commands still in flight on it.
                exception = ex;
            }

            if (Stopwatch.GetElapsedTime(start) + backoff >= _operationTimeout)
            {
                throw new TimeoutException($"Garnet journal partition '{partition}' was unavailable for {_operationTimeout} running {command}.", exception);
            }

            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            await TryRefreshAsync(GetRemaining(start), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets a value indicating whether an exception from a partition means that the command may not have run and can be retried.
    /// </summary>
    public static bool IsTransient(Exception exception)
        => exception is RedisConnectionException or RedisTimeoutException or ObjectDisposedException
            || exception is RedisServerException && string.Equals(exception.Message, TransactionFailedMessage, StringComparison.Ordinal);

    private async ValueTask<RedisResult[]> ExecuteCoreAsync(int bucket, GarnetJournalKeySet? keys, string procedure, object[] args, StrongBox<bool>? ambiguous, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAttemptsAsync(bucket, keys, procedure, args, cancellationToken, ambiguous).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not ObjectDisposedException && IsStopped && !cancellationToken.IsCancellationRequested)
        {
            // Disposal marks the router stopped before it closes connections, so a failure observed once it is stopped
            // is attributed to disposal. Closing a connection fails the commands in flight on it in several ways,
            // including cancellation, so the original failure is kept as the inner exception.
            MarkAmbiguous(ambiguous);
            throw new ObjectDisposedException($"The {nameof(GarnetJournalRouter)} was disposed while procedure {procedure} was in progress.", exception);
        }
    }

    private async ValueTask<RedisResult[]> ExecuteAttemptsAsync(int bucket, GarnetJournalKeySet? keys, string procedure, object[] args, CancellationToken cancellationToken, StrongBox<bool>? ambiguous)
    {
        // Every wait is bounded by the operation's deadline: a stalled connection, command, or MetaStore refresh ends
        // the operation with a TimeoutException, while caller cancellation still surfaces as cancellation.
        var start = Stopwatch.GetTimestamp();
        var backoff = InitialBackoff;
        RangeMapSnapshot view;
        PartitionId partition;
        string reason;
        Exception? exception;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(IsStopped, this);
            view = View;
            partition = view.Map.GetOwner(bucket);
            exception = null;
            if (GetConnection(view, partition) is not { } connection)
            {
                // The partition's host has not published its endpoint in this view.
                reason = "unavailable";
            }
            else
            {
                try
                {
                    var database = await connection.GetDatabaseAsync().WaitAsync(GetRemaining(start), cancellationToken).ConfigureAwait(false);
                    var result = await database.ExecuteAsync(procedure, args).WaitAsync(GetRemaining(start), cancellationToken).ConfigureAwait(false);
                    ThrowIfReplyDropped(procedure);
                    var values = (RedisResult[]?)result is { Length: > 0 } array
                        ? array
                        : throw new InvalidOperationException($"Garnet journal procedure {procedure} returned an invalid response.");
                    var status = GetStatus(values);
                    if (status == GarnetJournalProtocol.NeedPullStatus && keys is not null)
                    {
                        reason = "need_pull";
                        if (await Migrator.TryPullAsync(keys, partition, values, cancellationToken).WaitAsync(GetRemaining(start), cancellationToken).ConfigureAwait(false))
                        {
                            // The partition now holds the journal, its tombstone, or an absent marker.
                            _telemetry.OnRetry(ProviderName, reason);
                            continue;
                        }
                    }
                    else if (status != GarnetJournalProtocol.WrongPartitionStatus)
                    {
                        return values;
                    }
                    else
                    {
                        reason = "wrong_partition";
                        var reportedVersion = GetReportedVersion(values);
                        if (reportedVersion > view.Version
                            && (await TryRefreshAsync(GetRemaining(start), cancellationToken).ConfigureAwait(false)).Version >= reportedVersion)
                        {
                            // The bucket moved in a view newer than the one used, and the router now has that view.
                            _telemetry.OnRetry(ProviderName, reason);
                            continue;
                        }
                    }
                }
                catch (RedisConnectionException ex)
                {
                    reason = "connection";
                    exception = ex;
                    MarkAmbiguous(ambiguous);
                }
                catch (RedisTimeoutException ex)
                {
                    reason = "timeout";
                    exception = ex;
                    MarkAmbiguous(ambiguous);
                }
                catch (TimeoutException ex)
                {
                    // The operation's deadline passed while waiting, possibly for the reply to a command already sent.
                    reason = "deadline";
                    exception = ex;
                    MarkAmbiguous(ambiguous);
                    break;
                }
                catch (ObjectDisposedException ex) when (!IsStopped)
                {
                    // The view changed the partition's endpoint and the old connection was closed.
                    reason = "connection";
                    exception = ex;
                    MarkAmbiguous(ambiguous);
                }
                catch (OperationCanceledException ex) when (!IsStopped && !cancellationToken.IsCancellationRequested)
                {
                    // Closing a connection retired by a view change cancels the commands still in flight on it.
                    reason = "connection";
                    exception = ex;
                    MarkAmbiguous(ambiguous);
                }
                catch (RedisServerException ex) when (string.Equals(ex.Message, TransactionFailedMessage, StringComparison.Ordinal))
                {
                    // The transaction did not run, for example because its locks were not acquired in time.
                    reason = "transaction";
                    exception = ex;
                }
            }

            if (Stopwatch.GetElapsedTime(start) + backoff >= _operationTimeout)
            {
                break;
            }

            _telemetry.OnRetry(ProviderName, reason);
            _logger?.LogDebug(exception, "Retrying Garnet journal procedure {Procedure} on bucket {Bucket} after {Reason} failure at partition {Partition} in view {Version}.", procedure, bucket, reason, partition, view.Version);
            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            if (reason is not "transaction")
            {
                try
                {
                    await TryRefreshAsync(GetRemaining(start), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    exception = ex;
                    break;
                }
            }
        }

        throw new TimeoutException(
            $"Garnet journal procedure {procedure} on bucket {bucket} did not complete within {_operationTimeout}: the last attempt, on partition '{partition}' in view {view.Version}, failed with reason '{reason}'.",
            exception);
    }
    /// <summary>
    /// Reads the MetaStore and applies its map if it is newer than the current view.
    /// </summary>
    /// <remarks>
    /// Concurrent callers share one read. A caller which stops waiting, through cancellation or a timeout, does not
    /// cancel the shared read.
    /// </remarks>
    /// <returns>The current view after the refresh.</returns>
    public Task<RangeMapSnapshot> RefreshAsync(CancellationToken cancellationToken) => StartRefresh().WaitAsync(cancellationToken);

    private Task<RangeMapSnapshot> StartRefresh()
    {
        TaskCompletionSource<RangeMapSnapshot>? owner = null;
        Task<RangeMapSnapshot> refresh;
        lock (_lock)
        {
            if (_refresh is null)
            {
                owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _refresh = owner.Task;
            }

            refresh = _refresh;
        }

        if (owner is not null)
        {
            _ = RunRefreshAsync(owner);
        }

        return refresh;
    }

    /// <summary>
    /// Applies <paramref name="snapshot"/> if it is newer than the current view.
    /// </summary>
    /// <returns><see langword="true"/> if the view changed.</returns>
    /// <exception cref="InvalidOperationException">The snapshot's map is invalid, or changes the map's identity or bucket count.</exception>
    public bool TryApplyView(RangeMapSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Map.Validate();
        if (snapshot.Map.BucketCount != BucketCount || !string.Equals(snapshot.Map.Id, MapId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Range map version {snapshot.Version} is map '{snapshot.Map.Id}' with {snapshot.Map.BucketCount} buckets, but the cluster uses map '{MapId}' with {BucketCount}. A map's identity and bucket count never change.");
        }

        lock (_lock)
        {
            if (_stopped || snapshot.Version <= _view.Version)
            {
                return false;
            }

            Volatile.Write(ref _view, snapshot);
            var endpoints = snapshot.Map.Partitions.Values.Select(hosting => hosting.Endpoint).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var (endpoint, connection) in _connections.ToList())
            {
                if (!endpoints.Contains(endpoint))
                {
                    _connections.Remove(endpoint);
                    _retiring.RemoveAll(task => task.IsCompleted);
                    _retiring.Add(RetireAsync(connection));
                }
            }
        }

        _logger?.LogDebug("Applied Garnet journal range map version {Version}.", snapshot.Version);
        return true;
    }

    /// <summary>
    /// Closes every connection. Operations whose commands are in flight fail with <see cref="ObjectDisposedException"/>
    /// at once; other operations in progress fail with it at their next attempt. No connection is opened after
    /// disposal begins.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        List<PartitionConnection> connections;
        List<Task> retiring;
        lock (_lock)
        {
            if (_stopped)
            {
                return;
            }

            Volatile.Write(ref _stopped, true);
            connections = [.. _connections.Values];
            _connections.Clear();
            retiring = [.. _retiring];
        }

        _shutdown.Cancel();
        foreach (var connection in connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(retiring).ConfigureAwait(false);
    }

    /// <summary>Gets the number of open partition connections, excluding connections being retired.</summary>
    internal int ConnectionCount
    {
        get
        {
            lock (_lock)
            {
                return _connections.Count;
            }
        }
    }

    private bool IsStopped => Volatile.Read(ref _stopped);

    private void ThrowIfReplyDropped(string command)
    {
        if (DropReply?.Invoke(command) == true)
        {
            throw new RedisTimeoutException($"Injected loss of the reply to {command}.", CommandStatus.Sent);
        }
    }

    public static int GetStatus(RedisResult[] result)
        => int.TryParse(result[0].ToString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var status)
            ? status
            : throw new InvalidOperationException("A Garnet journal procedure returned an invalid status.");

    private static long GetReportedVersion(RedisResult[] result)
        => result.Length >= 3 && long.TryParse(result[2].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : throw new InvalidOperationException("A Garnet journal procedure returned a malformed WrongPartition response.");

    private PartitionConnection? GetConnection(RangeMapSnapshot view, PartitionId partition)
    {
        if (!view.Map.Partitions.TryGetValue(partition, out var hosting) || hosting.Endpoint is not { } endpoint)
        {
            return null;
        }

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (!_connections.TryGetValue(endpoint, out var connection))
            {
                // Connect only to endpoints in the current view, so connections from stale views are not retained.
                if (!_view.Map.Partitions.TryGetValue(partition, out var current) || current.Endpoint != endpoint)
                {
                    return null;
                }

                connection = new PartitionConnection(endpoint);
                _connections.Add(endpoint, connection);
            }

            return connection;
        }
    }

    /// <summary>
    /// Refreshes the view, waiting at most <paramref name="timeout"/>.
    /// </summary>
    /// <returns>The current view, which is unchanged if the refresh failed.</returns>
    /// <exception cref="TimeoutException">The refresh did not complete within <paramref name="timeout"/>.</exception>
    private async Task<RangeMapSnapshot> TryRefreshAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var refresh = StartRefresh();
        try
        {
            return await refresh.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (refresh.IsFaulted)
        {
            // Disposal cancels the shared refresh.
            ObjectDisposedException.ThrowIf(IsStopped, this);
            _logger?.LogWarning(exception, "Failed to refresh the Garnet journal range map.");
            return View;
        }
    }

    private static void MarkAmbiguous(StrongBox<bool>? ambiguous)
    {
        if (ambiguous is not null)
        {
            ambiguous.Value = true;
        }
    }

    private TimeSpan GetRemaining(long start)
    {
        var remaining = _operationTimeout - Stopwatch.GetElapsedTime(start);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private async Task RunRefreshAsync(TaskCompletionSource<RangeMapSnapshot> completion)
    {
        try
        {
            var snapshot = await _metaStore.ReadAsync(_shutdown.Token).ConfigureAwait(false);
            if (snapshot is not null)
            {
                TryApplyView(snapshot);
            }

            lock (_lock)
            {
                _refresh = null;
            }

            completion.SetResult(View);
        }
        catch (Exception exception)
        {
            lock (_lock)
            {
                _refresh = null;
            }

            completion.SetException(exception);
        }
    }

    private async Task RetireAsync(PartitionConnection connection)
    {
        // Operations in flight on the old endpoint may still complete; close the connection once they have timed out.
        try
        {
            await Task.Delay(_operationTimeout, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>A connection to a partition endpoint.</summary>
    private sealed class PartitionConnection(string endpoint) : IAsyncDisposable
    {
        private readonly Task<ConnectionMultiplexer> _connecting = ConnectionMultiplexer.ConnectAsync(CreateConfiguration(endpoint));

        public async Task<IDatabase> GetDatabaseAsync() => (await _connecting.ConfigureAwait(false)).GetDatabase();

        public async ValueTask DisposeAsync()
        {
            ConnectionMultiplexer multiplexer;
            try
            {
                multiplexer = await _connecting.ConfigureAwait(false);
            }
            catch (RedisException)
            {
                return;
            }

            await multiplexer.CloseAsync(allowCommandsToComplete: false).ConfigureAwait(false);
            multiplexer.Dispose();
        }

        private static ConfigurationOptions CreateConfiguration(string endpoint)
        {
            var configuration = new ConfigurationOptions
            {
                // Procedure responses are RESP2 arrays of bulk strings.
                Protocol = RedisProtocol.Resp2,
                AbortOnConnectFail = false,
            };
            configuration.EndPoints.Add(endpoint);
            return configuration;
        }
    }
}
