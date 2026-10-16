using System.Collections.Immutable;
using System.Net;
using Orleans.Journaling.Ranges;
using StackExchange.Redis;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Embedded partitions sharing one in-memory MetaStore, with routers and coordinators over them, for tests which move
/// buckets between partitions.
/// </summary>
internal sealed class GarnetTestPartitions : IAsyncDisposable
{
    private readonly string _directory = GarnetPartitionFixture.CreateTemporaryDirectory();
    private readonly Dictionary<PartitionId, GarnetJournalPartitionHost> _hosts = [];
    private readonly Dictionary<PartitionId, CrashHostProcess> _processes = [];
    private readonly bool _useProcesses;
    private readonly Dictionary<PartitionId, IPEndPoint> _endpoints = [];
    private readonly Dictionary<PartitionId, ConnectionMultiplexer> _admin = [];
    private readonly List<GarnetJournalRouter> _routers = [];

    private GarnetTestPartitions(IReadOnlyList<PartitionId> partitions, bool useProcesses)
    {
        Partitions = partitions;
        _useProcesses = useProcesses;
    }

    public IReadOnlyList<PartitionId> Partitions { get; }

    public InMemoryRangeMetaStore Store { get; } = new();

    public string MapId { get; private set; } = null!;

    public int BucketCount { get; private set; }

    public static PartitionId Id(int index) => new($"p{index}");

    /// <summary>
    /// Starts <paramref name="partitionCount"/> partitions. Buckets are divided uniformly among the first
    /// <paramref name="owners"/> partitions; every partition's endpoint is published. With
    /// <paramref name="useProcesses"/>, each partition runs in a child process which <see cref="KillHostAsync"/> kills.
    /// </summary>
    public static async Task<GarnetTestPartitions> StartAsync(int partitionCount, int bucketCount, int owners, CancellationToken cancellationToken, string? mapId = null, bool useProcesses = false)
    {
        var partitions = Enumerable.Range(0, partitionCount).Select(Id).ToArray();
        var result = new GarnetTestPartitions(partitions, useProcesses);
        try
        {
            foreach (var id in partitions)
            {
                await result.StartHostAsync(id, cancellationToken);
            }

            var map = RangeMap.CreateUniform(bucketCount, partitions.Take(owners).Select(Hosting));
            map = map with
            {
                Id = mapId ?? map.Id,
                Partitions = partitions.ToImmutableSortedDictionary(
                    id => id,
                    id => Hosting(id).Value with { Endpoint = GarnetJournalPartitionManager.FormatEndpoint(result._endpoints[id]) }),
            };
            result.MapId = map.Id;
            result.BucketCount = bucketCount;
            await result.Store.TryWriteAsync(map, 0, cancellationToken);
            foreach (var id in partitions)
            {
                await GarnetJournalPartitionManager.InitializeAsync(result._endpoints[id], id, map, cancellationToken);
            }

            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }

        static KeyValuePair<PartitionId, PartitionHosting> Hosting(PartitionId id) => new(id, new PartitionHosting("test", 0, id.Value));
    }

    public async Task<RangeMapSnapshot> ReadViewAsync() => (await Store.ReadAsync(CancellationToken.None))!;

    public GarnetJournalRouter CreateRouter(TimeSpan? operationTimeout = null, TimeProvider? timeProvider = null)
    {
        var view = Store.ReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult()!;
        var router = new GarnetJournalRouter(Store, view, operationTimeout ?? TimeSpan.FromSeconds(10), timeProvider: timeProvider);
        lock (_routers)
        {
            _routers.Add(router);
        }

        return router;
    }

    public IDatabase Database(PartitionId partition) => _admin[partition].GetDatabase();

    public IPEndPoint EndPoint(PartitionId partition) => _endpoints[partition];

    /// <summary>Stops a partition, killing its process if it has one, keeping its durable state.</summary>
    public async Task KillHostAsync(PartitionId partition)
    {
        if (_admin.Remove(partition, out var admin))
        {
            await admin.CloseAsync(allowCommandsToComplete: false);
            admin.Dispose();
        }

        if (_processes.Remove(partition, out var process))
        {
            process.Dispose();
        }

        if (_hosts.Remove(partition, out var host))
        {
            host.Dispose();
        }
    }

    /// <summary>
    /// Starts a stopped partition on a new port, recovering its state from its data directory, and publishes its new
    /// endpoint in the range map, as its host would.
    /// </summary>
    public async Task RestartHostAsync(PartitionId partition, CancellationToken cancellationToken)
    {
        await StartHostAsync(partition, cancellationToken);
        var endpoint = GarnetJournalPartitionManager.FormatEndpoint(_endpoints[partition]);
        while (true)
        {
            var current = await ReadViewAsync();
            var map = current.Map with { Partitions = current.Map.Partitions.SetItem(partition, current.Map.Partitions[partition] with { Endpoint = endpoint }) };
            if ((await Store.TryWriteAsync(map, current.Version, cancellationToken)).Written)
            {
                return;
            }
        }
    }

    private async Task StartHostAsync(PartitionId partition, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_directory, partition.Value);
        IPEndPoint endPoint;
        if (_useProcesses)
        {
            var process = await CrashHostProcess.StartAsync(directory, cancellationToken);
            _processes[partition] = process;
            endPoint = process.EndPoint;
        }
        else
        {
            var host = GarnetJournalPartitionHost.Start(
                new GarnetJournalPartitionSettings { DataDirectory = directory, LogMemorySize = "64m", IndexMemorySize = "1m" },
                loggerFactory: null);
            _hosts[partition] = host;
            endPoint = host.EndPoint;
        }

        _endpoints[partition] = endPoint;
        _admin[partition] = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { endPoint },
            Protocol = RedisProtocol.Resp2,
            AbortOnConnectFail = false,
            AllowAdmin = true,
        }).WaitAsync(cancellationToken);
    }

    /// <summary>Finds journal ids, with the given prefix, whose buckets are <paramref name="bucket"/>.</summary>
    public IEnumerable<JournalId> JournalsInBucket(int bucket, string prefix)
    {
        for (var i = 0; ; i++)
        {
            var id = JournalId.Create(prefix, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (GarnetJournalKeys.GetBucket(GarnetJournalKeys.EncodeJournalId(id.Value), BucketCount) == bucket)
            {
                yield return id;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var router in _routers)
        {
            await router.DisposeAsync();
        }

        foreach (var partition in Partitions)
        {
            await KillHostAsync(partition);
        }

        GarnetPartitionFixture.TryDeleteDirectory(_directory);
    }
}
