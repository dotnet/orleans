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
    public GarnetJournalRouter(
        IRangeMetaStore metaStore,
        RangeMapSnapshot view,
        TimeSpan operationTimeout,
        JournalStorageTelemetry? telemetry = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(metaStore);
        ArgumentNullException.ThrowIfNull(view);
        view.Map.Validate();
        _metaStore = metaStore;
        _view = view;
        _operationTimeout = operationTimeout;
        _telemetry = telemetry ?? JournalStorageTelemetry.CreateForDirectConstruction();
        _logger = logger;
        BucketCount = view.Map.BucketCount;
        MapId = view.Map.Id;
    }

    /// <summary>Gets the number of buckets, which is the same in every view.</summary>
    public int BucketCount { get; }

    /// <summary>Gets the identity of the range map, which is the same in every view.</summary>
    public string MapId { get; }

    /// <summary>Gets the current view.</summary>
    public RangeMapSnapshot View => Volatile.Read(ref _view);

    /// <summary>
    /// Runs a journal procedure on the partition which owns <paramref name="bucket"/>.
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
    public async ValueTask<RedisResult[]> ExecuteAsync(int bucket, string procedure, object[] args, CancellationToken cancellationToken, StrongBox<bool>? ambiguous = null)
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
                    var values = (RedisResult[]?)result is { Length: > 0 } array
                        ? array
                        : throw new InvalidOperationException($"Garnet journal procedure {procedure} returned an invalid response.");
                    if (GetStatus(values) != GarnetJournalProtocol.WrongPartitionStatus)
                    {
                        return values;
                    }

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
    /// Closes every connection. Operations in progress fail with <see cref="ObjectDisposedException"/> at their next
    /// attempt, and no connection is opened after disposal begins.
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
