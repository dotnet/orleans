using System.Diagnostics;
using System.Globalization;
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
    }

    /// <summary>Gets the number of buckets, which is the same in every view.</summary>
    public int BucketCount { get; }

    /// <summary>Gets the current view.</summary>
    public RangeMapSnapshot View => Volatile.Read(ref _view);

    /// <summary>
    /// Runs a journal procedure on the partition which owns <paramref name="bucket"/>.
    /// </summary>
    /// <returns>The procedure's response, whose status is not <c>WrongPartition</c>.</returns>
    /// <exception cref="TimeoutException">The operation did not complete within the operation timeout.</exception>
    public async ValueTask<RedisResult[]> ExecuteAsync(int bucket, string procedure, object[] args, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var backoff = InitialBackoff;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var view = View;
            var partition = view.Map.GetOwner(bucket);
            string reason;
            Exception? exception = null;
            if (GetConnection(view, partition) is not { } connection)
            {
                // The partition's host has not published its endpoint in this view.
                reason = "unavailable";
            }
            else
            {
                try
                {
                    var database = await connection.GetDatabaseAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                    var result = await database.ExecuteAsync(procedure, args).WaitAsync(cancellationToken).ConfigureAwait(false);
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
                        && (await TryRefreshAsync(cancellationToken).ConfigureAwait(false)).Version >= reportedVersion)
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
                }
                catch (RedisTimeoutException ex)
                {
                    reason = "timeout";
                    exception = ex;
                }
                catch (ObjectDisposedException ex)
                {
                    // The view changed the partition's endpoint and the old connection was closed.
                    reason = "connection";
                    exception = ex;
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
                throw new TimeoutException(
                    $"Garnet journal procedure {procedure} on bucket {bucket} did not complete within {_operationTimeout}: the last attempt, on partition '{partition}' in view {view.Version}, failed with reason '{reason}'.",
                    exception);
            }

            _telemetry.OnRetry(ProviderName, reason);
            _logger?.LogDebug(exception, "Retrying Garnet journal procedure {Procedure} on bucket {Bucket} after {Reason} failure at partition {Partition} in view {Version}.", procedure, bucket, reason, partition, view.Version);
            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            if (reason is not "transaction")
            {
                await TryRefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reads the MetaStore and applies its map if it is newer than the current view.
    /// </summary>
    /// <remarks>Concurrent callers share one read.</remarks>
    /// <returns>The current view after the refresh.</returns>
    public Task<RangeMapSnapshot> RefreshAsync(CancellationToken cancellationToken)
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

        return refresh.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Applies <paramref name="snapshot"/> if it is newer than the current view.
    /// </summary>
    /// <returns><see langword="true"/> if the view changed.</returns>
    /// <exception cref="InvalidOperationException">The snapshot's map is invalid or changes the bucket count.</exception>
    public bool TryApplyView(RangeMapSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Map.Validate();
        if (snapshot.Map.BucketCount != BucketCount)
        {
            throw new InvalidOperationException(
                $"Range map version {snapshot.Version} has {snapshot.Map.BucketCount} buckets, but the cluster has {BucketCount}. The bucket count never changes.");
        }

        lock (_lock)
        {
            if (snapshot.Version <= _view.Version)
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

    public async ValueTask DisposeAsync()
    {
        List<PartitionConnection> connections;
        List<Task> retiring;
        lock (_lock)
        {
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
        _shutdown.Dispose();
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

    private async Task<RangeMapSnapshot> TryRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "Failed to refresh the Garnet journal range map.");
            return View;
        }
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
