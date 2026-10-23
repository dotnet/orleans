using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Orleans.Journaling.Ranges;
using StackExchange.Redis;

namespace Orleans.Journaling;

/// <summary>
/// Hosts the partitions which the range map assigns to this silo, publishes their endpoints, and follows changes to
/// the assignment.
/// </summary>
/// <remarks>
/// <para>
/// Each hosted partition is an embedded Garnet server. Its durable state lives either under
/// <see cref="GarnetJournalStorageOptions.DataDirectory"/>, in the partition's <see cref="PartitionHosting.StorageLocation"/>,
/// or, with <see cref="GarnetJournalStorageOptions.PartitionBlobContainer"/>, in page blobs under that location, which
/// any silo can take over (D12). When the manager starts a partition, it opens the buckets the map assigns to it, then
/// publishes the partition's endpoint in the map, so other silos route to the partition only once it serves its buckets.
/// </para>
/// <para>
/// The manager reads the range map every <see cref="GarnetJournalStorageOptions.HostingInterval"/>. It stops a partition
/// as soon as it observes a view which assigns the partition to another host or another epoch, or once the partition's
/// storage fails or is fenced, and starts every partition the view assigns to this host which it does not serve. A
/// partition on blob storage starts by taking its storage over at the epoch of its assignment, which fences every
/// earlier host before recovery reads the storage.
/// </para>
/// </remarks>
internal sealed class GarnetJournalPartitionManager : IAsyncDisposable
{
    private readonly GarnetJournalStorageOptions _options;
    private readonly string? _hostId;
    private readonly IRangeMetaStore _metaStore;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger? _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<PartitionId, HostedPartition> _partitions = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private RangeMapSnapshot _view;
    private Task? _run;

    private GarnetJournalPartitionManager(
        GarnetJournalStorageOptions options,
        string? hostId,
        IRangeMetaStore metaStore,
        RangeMapSnapshot view,
        ILoggerFactory? loggerFactory)
    {
        _options = options;
        _hostId = hostId;
        _metaStore = metaStore;
        _view = view;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<GarnetJournalPartitionManager>();
    }

    /// <summary>Gets the partitions this silo serves.</summary>
    public IReadOnlyDictionary<PartitionId, GarnetJournalPartitionHost> Hosts
    {
        get
        {
            lock (_lock)
            {
                return _partitions.ToDictionary(entry => entry.Key, entry => entry.Value.Host);
            }
        }
    }

    /// <summary>Gets the latest view this manager has read or published.</summary>
    public RangeMapSnapshot View => Volatile.Read(ref _view);

    /// <summary>Gets or sets a callback which receives every view the manager reads or publishes.</summary>
    public Action<RangeMapSnapshot>? ViewObserved { get; set; }

    /// <summary>Gets a value indicating whether this silo serves <paramref name="partition"/>.</summary>
    public bool IsHosted(PartitionId partition)
    {
        lock (_lock)
        {
            return _partitions.ContainsKey(partition);
        }
    }

    /// <summary>
    /// Starts every partition which <paramref name="view"/> assigns to <paramref name="hostId"/>, if any, and publishes their endpoints.
    /// </summary>
    public static async Task<GarnetJournalPartitionManager> StartAsync(
        GarnetJournalStorageOptions options,
        string? hostId,
        IRangeMetaStore metaStore,
        RangeMapSnapshot view,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken)
    {
        var manager = new GarnetJournalPartitionManager(options, hostId, metaStore, view, loggerFactory);
        try
        {
            await manager.ReconcileAsync(view, throwOnFailure: true, cancellationToken).ConfigureAwait(false);
            return manager;
        }
        catch
        {
            await manager.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Starts following the range map: hosting, stopping, and restarting partitions as their assignments change.</summary>
    public void StartMonitoring() => _run = Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Reads the range map and applies its hosting assignments now.</summary>
    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var view = await _metaStore.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Garnet journal range map was removed from its MetaStore.");
        view.Map.Validate();
        await ReconcileAsync(view, throwOnFailure: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes a new partition for <paramref name="map"/>, or verifies and completes the initialization of an
    /// existing one.
    /// </summary>
    /// <remarks>
    /// The partition first claims its identity (map, partition id, bucket count) in its descriptor, as one field set
    /// only if absent, then opens the buckets the map assigns to it, then marks the descriptor initialized. Storage
    /// which records another identity is rejected before any bucket is opened, so a data directory serves only the
    /// partition it was created for, and of concurrent initializations with different identities exactly one succeeds.
    /// Opening a bucket is idempotent, so an initialization interrupted by a crash resumes on the next start. Buckets
    /// which a migration moves into the partition are opened by the migration.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The storage belongs to another partition or map.</exception>
    public static async Task InitializeAsync(EndPoint endPoint, PartitionId partitionId, RangeMap map, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { endPoint },
            Protocol = RedisProtocol.Resp2,
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        var database = connection.GetDatabase();
        var bucketCount = map.BucketCount.ToString(CultureInfo.InvariantCulture);

        // Claim the whole identity as one field, set only if absent, before any bucket is opened. Claiming its parts
        // separately could leave a mixed identity which no initialization matches.
        var identity = FormatIdentity(map.Id, partitionId.Value, bucketCount);
        await database.HashSetAsync(GarnetJournalKeys.PartitionKey, GarnetJournalKeys.IdentityField, identity, When.NotExists)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        var descriptor = await ReadDescriptorAsync(database, cancellationToken).ConfigureAwait(false);
        var mapId = descriptor.GetValueOrDefault(GarnetJournalKeys.MapIdField);
        var id = descriptor.GetValueOrDefault(GarnetJournalKeys.PartitionIdField);
        var claimedBucketCount = descriptor.GetValueOrDefault(GarnetJournalKeys.BucketCountField);
        if (!string.Equals(descriptor.GetValueOrDefault(GarnetJournalKeys.IdentityField), identity, StringComparison.Ordinal)
            || mapId is not null && !string.Equals(mapId, map.Id, StringComparison.Ordinal)
            || id is not null && !string.Equals(id, partitionId.Value, StringComparison.Ordinal)
            || claimedBucketCount is not null && !string.Equals(claimedBucketCount, bucketCount, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The storage of Garnet journal partition '{partitionId}' holds '{descriptor.GetValueOrDefault(GarnetJournalKeys.IdentityField)}' (partition '{id}' of map '{mapId}' with {claimedBucketCount} buckets), but the range map expects partition '{partitionId}' of map '{map.Id}' with {map.BucketCount} buckets.");
        }

        if (descriptor.ContainsKey(GarnetJournalKeys.InitializedField))
        {
            return;
        }

        // Record the claimed identity's parts for inspection; they never differ from the claim.
        await database.HashSetAsync(
            GarnetJournalKeys.PartitionKey,
            [
                new(GarnetJournalKeys.MapIdField, map.Id),
                new(GarnetJournalKeys.PartitionIdField, partitionId.Value),
                new(GarnetJournalKeys.BucketCountField, bucketCount),
                new(GarnetJournalKeys.SchemaVersionField, GarnetJournalProtocol.CurrentSchemaVersion),
            ]).WaitAsync(cancellationToken).ConfigureAwait(false);

        const int BatchSize = 256;
        var buckets = GetInitialBuckets(map, partitionId).ToList();
        var tasks = new List<Task<RedisResult>>(BatchSize);
        for (var start = 0; start < buckets.Count; start += BatchSize)
        {
            tasks.Clear();
            foreach (var (bucket, version) in buckets.Skip(start).Take(BatchSize))
            {
                tasks.Add(database.ExecuteAsync(
                    GarnetJournalProtocol.Bucket,
                    GarnetJournalKeySet.GetBucketArgs(map.Id, bucket, GarnetJournalProtocol.BucketOpenOperation, version.ToString(CultureInfo.InvariantCulture), GarnetJournalProtocol.NoSource, "1")));
            }

            foreach (var result in await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                var values = (RedisResult[])result!;
                // A bucket which is already open, at any version, was opened by an earlier initialization.
                if (GarnetJournalRouter.GetStatus(values) != GarnetJournalProtocol.SuccessStatus && values.ElementAtOrDefault(1)?.ToString() != "open")
                {
                    throw new InvalidOperationException(
                        $"Garnet journal partition '{partitionId}' could not open a bucket: it is {values.ElementAtOrDefault(1)} at version {values.ElementAtOrDefault(2)}.");
                }
            }
        }

        await database.HashSetAsync(GarnetJournalKeys.PartitionKey, GarnetJournalKeys.InitializedField, "1").WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops every hosted partition. Their durable state remains in their storage.</summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_run is { } run)
        {
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        List<HostedPartition> partitions;
        lock (_lock)
        {
            partitions = [.. _partitions.Values];
            _partitions.Clear();
        }

        foreach (var partition in partitions)
        {
            partition.Dispose();
        }
    }

    /// <summary>Gets the buckets assigned to <paramref name="partitionId"/> which no migration moves into it, with their assignment versions.</summary>
    internal static IEnumerable<(int Bucket, long Version)> GetInitialBuckets(RangeMap map, PartitionId partitionId)
    {
        foreach (var range in map.Ranges)
        {
            if (range.Owner != partitionId)
            {
                continue;
            }

            for (var bucket = range.FirstBucket; bucket <= range.LastBucket; bucket++)
            {
                if (!map.Migrations.Any(migration => migration.FirstBucket <= bucket && bucket <= migration.LastBucket))
                {
                    yield return (bucket, range.AssignedAtVersion);
                }
            }
        }
    }

    /// <summary>
    /// Resolves a partition's storage location, a relative path, against the silo's data directory.
    /// </summary>
    internal static string ResolveStorageLocation(string dataDirectory, PartitionId partitionId, string storageLocation)
    {
        var root = Path.GetFullPath(dataDirectory);
        var path = Path.GetFullPath(Path.Combine(root, storageLocation));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(storageLocation) || relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException(
                $"Garnet journal partition '{partitionId}' has storage location '{storageLocation}', which is not a relative path within the data directory.");
        }

        return path;
    }

    /// <summary>
    /// Gets the address at which this process connects to a partition it hosts: the loopback address of the same
    /// family for a wildcard listen address, which is not a valid connection destination on every platform.
    /// </summary>
    internal static IPEndPoint GetLocalEndPoint(IPEndPoint endPoint)
    {
        if (endPoint.Address.Equals(IPAddress.Any))
        {
            return new IPEndPoint(IPAddress.Loopback, endPoint.Port);
        }

        return endPoint.Address.Equals(IPAddress.IPv6Any) ? new IPEndPoint(IPAddress.IPv6Loopback, endPoint.Port) : endPoint;
    }

    internal static string FormatEndpoint(IPEndPoint endPoint)
    {
        var host = endPoint.Address.Equals(IPAddress.Any) || endPoint.Address.Equals(IPAddress.IPv6Any)
            ? Dns.GetHostName()
            : endPoint.Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{endPoint.Address}]" : endPoint.Address.ToString();
        return $"{host}:{endPoint.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Formats a partition identity as recorded in the descriptor's identity field.</summary>
    internal static string FormatIdentity(string mapId, string partitionId, string bucketCount) => $"{mapId}/{partitionId}/{bucketCount}";

    private static async Task<Dictionary<string, string>> ReadDescriptorAsync(IDatabase database, CancellationToken cancellationToken)
        => (await database.HashGetAllAsync(GarnetJournalKeys.PartitionKey).WaitAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(entry => entry.Name.ToString(), entry => entry.Value.ToString(), StringComparer.Ordinal);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(_options.HostingInterval, cancellationToken).ConfigureAwait(false);
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Applying Garnet journal partition hosting assignments failed; retrying.");
            }
        }
    }

    /// <summary>
    /// Stops the partitions <paramref name="view"/> no longer assigns to this host at their epoch, and those whose
    /// storage failed; starts those it assigns to this host which are not running; and publishes their endpoints.
    /// </summary>
    private async Task ReconcileAsync(RangeMapSnapshot view, bool throwOnFailure, CancellationToken cancellationToken)
    {
        if (view.Version > View.Version)
        {
            Volatile.Write(ref _view, view);
        }

        view = View;
        ViewObserved?.Invoke(view);
        var assigned = _hostId is null
            ? []
            : view.Map.Partitions.Where(entry => string.Equals(entry.Value.Host, _hostId, StringComparison.Ordinal)).ToDictionary(entry => entry.Key, entry => entry.Value);
        List<HostedPartition> stopping = [];
        lock (_lock)
        {
            foreach (var (id, partition) in _partitions.ToList())
            {
                if (!assigned.TryGetValue(id, out var hosting) || hosting.Epoch != partition.Epoch || partition.HasFailed)
                {
                    _partitions.Remove(id);
                    stopping.Add(partition);
                }
            }
        }

        foreach (var partition in stopping)
        {
            _logger?.LogInformation(
                "Host {HostId} stops serving Garnet journal partition {Partition} at epoch {Epoch}: {Reason}.",
                _hostId,
                partition.Id,
                partition.Epoch,
                partition.HasFailed ? $"its storage failed ({partition.Storage?.Failure?.Message})" : $"range map version {view.Version} assigns it elsewhere");
            partition.Dispose();
        }

        if (assigned.Count > 0 && string.IsNullOrWhiteSpace(_options.DataDirectory) && _options.PartitionBlobContainer is null)
        {
            throw new InvalidOperationException(
                $"The Garnet journal range map assigns partitions to host '{_hostId}', which has neither {nameof(GarnetJournalStorageOptions)}.{nameof(GarnetJournalStorageOptions.DataDirectory)} nor {nameof(GarnetJournalStorageOptions.PartitionBlobContainer)}.");
        }

        // A takeover fences every blob under its partition's location, so it must not reach another partition's blobs.
        if (assigned.Count > 0 && _options.PartitionBlobContainer is not null)
        {
            GarnetPartitionBlobStorage.ValidateStorageLocations(view.Map.Partitions);
        }

        foreach (var (id, hosting) in assigned.OrderBy(entry => entry.Key))
        {
            if (IsHosted(id))
            {
                continue;
            }

            try
            {
                var partition = await StartPartitionAsync(id, hosting, view.Map, cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    _partitions.Add(id, partition);
                }
            }
            catch (GarnetPartitionFencedException exception) when (!throwOnFailure)
            {
                // A later epoch holds the storage; this host's assignment is stale and the view will follow.
                _logger?.LogInformation(exception, "Host {HostId} does not serve Garnet journal partition {Partition} at epoch {Epoch}.", _hostId, id, hosting.Epoch);
            }
            catch (Exception exception) when (!throwOnFailure && exception is not OperationCanceledException)
            {
                _logger?.LogWarning(exception, "Host {HostId} could not start Garnet journal partition {Partition} at epoch {Epoch}; retrying.", _hostId, id, hosting.Epoch);
            }
        }

        await PublishEndpointsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HostedPartition> StartPartitionAsync(PartitionId id, PartitionHosting hosting, RangeMap map, CancellationToken cancellationToken)
    {
        GarnetPartitionBlobStorage? storage = null;
        string? directory = null;
        if (_options.PartitionBlobContainer is { } container)
        {
            // Fence every earlier host before recovery reads the storage.
            storage = await GarnetPartitionBlobStorage.OpenAsync(
                container,
                hosting.StorageLocation,
                hosting.Epoch,
                _loggerFactory?.CreateLogger<GarnetPartitionBlobStorage>(),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            directory = ResolveStorageLocation(_options.DataDirectory!, id, hosting.StorageLocation);
        }

        var host = GarnetJournalPartitionHost.Start(
            new GarnetJournalPartitionSettings
            {
                DataDirectory = directory,
                BlobStorage = storage,
                ListenEndPoint = GetListenEndPoint(),
                LogMemorySize = _options.LogMemorySize,
                IndexMemorySize = _options.IndexMemorySize,
                CheckpointAofSize = _options.CheckpointAofSize,
            },
            _loggerFactory);
        var partition = new HostedPartition(id, hosting.Epoch, host, storage);
        try
        {
            await InitializeAsync(GetLocalEndPoint(host.EndPoint), id, map, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            partition.Dispose();
            throw;
        }

        // Re-examine the assignment as soon as the storage fails, rather than at the next interval.
        storage?.Failed.Register(static state => ((SemaphoreSlim)state!).Release(), _wake);
        _logger?.LogInformation("Host {HostId} serves Garnet journal partition {Partition} at epoch {Epoch} on {EndPoint}.", _hostId, id, hosting.Epoch, host.EndPoint);
        return partition;
    }

    /// <summary>Gets an endpoint for a new partition: a free port, or the first port from the configured one not in use.</summary>
    private IPEndPoint GetListenEndPoint()
    {
        var listen = _options.ListenEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);
        if (listen.Port == 0)
        {
            return listen;
        }

        HashSet<int> used;
        lock (_lock)
        {
            used = [.. _partitions.Values.Select(partition => partition.Host.EndPoint.Port)];
        }

        var port = listen.Port;
        while (used.Contains(port))
        {
            port++;
        }

        return new IPEndPoint(listen.Address, port);
    }

    /// <summary>
    /// Publishes, by compare-and-swap, the endpoint of every served partition which the map still assigns to this host
    /// at the partition's epoch.
    /// </summary>
    private async Task PublishEndpointsAsync(CancellationToken cancellationToken)
    {
        Dictionary<PartitionId, (long Epoch, string Endpoint)> endpoints;
        lock (_lock)
        {
            endpoints = _partitions.ToDictionary(entry => entry.Key, entry => (entry.Value.Epoch, FormatEndpoint(entry.Value.Host.EndPoint)));
        }

        var view = View;
        while (endpoints.Count > 0)
        {
            var partitions = view.Map.Partitions;
            foreach (var (id, (epoch, endpoint)) in endpoints)
            {
                if (partitions.TryGetValue(id, out var hosting)
                    && string.Equals(hosting.Host, _hostId, StringComparison.Ordinal)
                    && hosting.Epoch == epoch
                    && hosting.Endpoint != endpoint)
                {
                    partitions = partitions.SetItem(id, hosting with { Endpoint = endpoint });
                }
            }

            if (partitions == view.Map.Partitions)
            {
                break;
            }

            RangeMapWriteResult result;
            try
            {
                result = await _metaStore.TryWriteAsync(view.Map with { Partitions = partitions }, view.Version, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The write's outcome is unknown: read the map and publish again if needed.
                result = new(false, await _metaStore.ReadAsync(cancellationToken).ConfigureAwait(false));
            }

            view = result.Current ?? throw new InvalidOperationException("The Garnet journal range map was removed from its MetaStore.");
            view.Map.Validate();
            if (view.Version > View.Version)
            {
                Volatile.Write(ref _view, view);
            }

            if (result.Written)
            {
                _logger?.LogInformation(
                    "Host {HostId} published Garnet journal partition endpoints {Endpoints} in range map version {Version}.",
                    _hostId,
                    string.Join(", ", endpoints.Select(entry => $"{entry.Key}@{entry.Value.Endpoint}")),
                    view.Version);
                break;
            }
        }

        ViewObserved?.Invoke(View);
    }

    /// <summary>A partition this host serves, at the epoch of its assignment.</summary>
    private sealed class HostedPartition(PartitionId id, long epoch, GarnetJournalPartitionHost host, GarnetPartitionBlobStorage? storage) : IDisposable
    {
        public PartitionId Id => id;

        public long Epoch => epoch;

        public GarnetJournalPartitionHost Host => host;

        public GarnetPartitionBlobStorage? Storage => storage;

        public bool HasFailed => storage?.Failure is not null;

        public void Dispose() => host.Dispose();
    }
}
