using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Orleans.Journaling.Ranges;
using StackExchange.Redis;

namespace Orleans.Journaling;

/// <summary>
/// Hosts the partitions which the range map assigns to this silo, and publishes their endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Each hosted partition is an embedded Garnet server whose durable state lives under
/// <see cref="GarnetJournalStorageOptions.DataDirectory"/>, in the partition's <see cref="PartitionHosting.StorageLocation"/>.
/// When the manager starts a partition, it opens the buckets the map assigns to it, then publishes the partition's
/// endpoint in the map, so other silos route to the partition only once it serves its buckets.
/// </para>
/// <para>
/// The range map is static in this version: buckets are opened only when a partition is first initialized, and the
/// hosting assignment of a partition never changes.
/// </para>
/// </remarks>
internal sealed class GarnetJournalPartitionManager : IDisposable
{
    private readonly Dictionary<PartitionId, GarnetJournalPartitionHost> _hosts;

    private GarnetJournalPartitionManager(Dictionary<PartitionId, GarnetJournalPartitionHost> hosts, RangeMapSnapshot view)
    {
        _hosts = hosts;
        View = view;
    }

    /// <summary>Gets the partitions this silo hosts.</summary>
    public IReadOnlyDictionary<PartitionId, GarnetJournalPartitionHost> Hosts => _hosts;

    /// <summary>Gets the view in which this silo published its partitions' endpoints.</summary>
    public RangeMapSnapshot View { get; }

    /// <summary>
    /// Starts every partition which <paramref name="view"/> assigns to <paramref name="hostId"/>, and publishes their endpoints.
    /// </summary>
    public static async Task<GarnetJournalPartitionManager> StartAsync(
        GarnetJournalStorageOptions options,
        string hostId,
        IRangeMetaStore metaStore,
        RangeMapSnapshot view,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken)
    {
        var hosted = view.Map.Partitions.Where(partition => string.Equals(partition.Value.Host, hostId, StringComparison.Ordinal)).ToList();
        var hosts = new Dictionary<PartitionId, GarnetJournalPartitionHost>();
        if (hosted.Count == 0)
        {
            return new(hosts, view);
        }

        if (string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            throw new InvalidOperationException(
                $"The Garnet journal range map assigns partitions to host '{hostId}', which has no {nameof(GarnetJournalStorageOptions)}.{nameof(GarnetJournalStorageOptions.DataDirectory)}.");
        }

        var logger = loggerFactory?.CreateLogger<GarnetJournalPartitionManager>();
        var manager = new GarnetJournalPartitionManager(hosts, view);
        try
        {
            var listen = options.ListenEndPoint ?? new IPEndPoint(IPAddress.Loopback, 0);
            var endpoints = new Dictionary<PartitionId, string>();
            for (var i = 0; i < hosted.Count; i++)
            {
                var (id, hosting) = hosted[i];
                var host = GarnetJournalPartitionHost.Start(
                    new GarnetJournalPartitionSettings
                    {
                        DataDirectory = ResolveStorageLocation(options.DataDirectory, id, hosting.StorageLocation),
                        ListenEndPoint = new IPEndPoint(listen.Address, listen.Port == 0 ? 0 : listen.Port + i),
                        LogMemorySize = options.LogMemorySize,
                        IndexMemorySize = options.IndexMemorySize,
                        CheckpointAofSize = options.CheckpointAofSize,
                    },
                    loggerFactory);
                hosts.Add(id, host);
                await InitializeAsync(host.EndPoint, id, view.Map, cancellationToken).ConfigureAwait(false);
                endpoints.Add(id, FormatEndpoint(host.EndPoint));
            }

            var published = await PublishEndpointsAsync(metaStore, hostId, endpoints, view, cancellationToken).ConfigureAwait(false);
            logger?.LogInformation(
                "Host {HostId} serves Garnet journal partitions {Partitions} in range map version {Version}.",
                hostId,
                string.Join(", ", endpoints.Select(entry => $"{entry.Key}@{entry.Value}")),
                published.Version);
            return new(hosts, published);
        }
        catch
        {
            manager.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the buckets <paramref name="map"/> assigns to a new partition and records the partition's descriptor, or
    /// verifies the descriptor of an initialized partition.
    /// </summary>
    /// <remarks>
    /// Opening a bucket is idempotent and the descriptor is written last, so a crash during initialization is repaired
    /// by the next initialization. Buckets which a migration moves into the partition are opened by the migration.
    /// </remarks>
    public static async Task InitializeAsync(EndPoint endPoint, PartitionId partitionId, RangeMap map, CancellationToken cancellationToken)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { endPoint },
            Protocol = RedisProtocol.Resp2,
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        var database = connection.GetDatabase();
        var descriptor = (await database.HashGetAllAsync(GarnetJournalKeys.PartitionKey).WaitAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(entry => entry.Name.ToString(), entry => entry.Value.ToString(), StringComparer.Ordinal);
        if (descriptor.Count > 0)
        {
            var bucketCount = descriptor.GetValueOrDefault(GarnetJournalKeys.BucketCountField);
            var id = descriptor.GetValueOrDefault(GarnetJournalKeys.PartitionIdField);
            if (!string.Equals(id, partitionId.Value, StringComparison.Ordinal)
                || !string.Equals(bucketCount, map.BucketCount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The storage of Garnet journal partition '{partitionId}' holds partition '{id}' with {bucketCount} buckets, but the range map expects partition '{partitionId}' with {map.BucketCount} buckets.");
            }

            return;
        }

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
                    [(RedisKey)GarnetJournalKeys.GetBucketKey(bucket), GarnetJournalProtocol.BucketOpenOperation, version.ToString(CultureInfo.InvariantCulture), GarnetJournalProtocol.NoSource, "1"]));
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

        await database.HashSetAsync(
            GarnetJournalKeys.PartitionKey,
            [
                new(GarnetJournalKeys.PartitionIdField, partitionId.Value),
                new(GarnetJournalKeys.BucketCountField, map.BucketCount),
                new(GarnetJournalKeys.SchemaVersionField, GarnetJournalProtocol.CurrentSchemaVersion),
            ]).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops every hosted partition. Their durable state remains in their storage locations.</summary>
    public void Dispose()
    {
        foreach (var host in _hosts.Values)
        {
            host.Dispose();
        }

        _hosts.Clear();
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

    internal static string FormatEndpoint(IPEndPoint endPoint)
    {
        var host = endPoint.Address.Equals(IPAddress.Any) || endPoint.Address.Equals(IPAddress.IPv6Any)
            ? Dns.GetHostName()
            : endPoint.Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{endPoint.Address}]" : endPoint.Address.ToString();
        return $"{host}:{endPoint.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    private static async Task<RangeMapSnapshot> PublishEndpointsAsync(
        IRangeMetaStore metaStore,
        string hostId,
        Dictionary<PartitionId, string> endpoints,
        RangeMapSnapshot view,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var partitions = view.Map.Partitions;
            foreach (var (id, endpoint) in endpoints)
            {
                // Publish only for partitions the latest view still assigns to this host.
                if (partitions.TryGetValue(id, out var hosting) && string.Equals(hosting.Host, hostId, StringComparison.Ordinal) && hosting.Endpoint != endpoint)
                {
                    partitions = partitions.SetItem(id, hosting with { Endpoint = endpoint });
                }
            }

            if (partitions == view.Map.Partitions)
            {
                return view;
            }

            var result = await metaStore.TryWriteAsync(view.Map with { Partitions = partitions }, view.Version, cancellationToken).ConfigureAwait(false);
            view = result.Current ?? throw new InvalidOperationException("The Garnet journal range map was removed from its MetaStore.");
            if (result.Written)
            {
                return view;
            }
        }
    }
}
