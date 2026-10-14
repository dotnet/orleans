using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Journaling.Ranges;
using StackExchange.Redis;

namespace Orleans.Journaling;

/// <summary>
/// Provides journal storage on Garnet partitions, hosting the partitions which the range map assigns to this silo.
/// </summary>
internal sealed class GarnetJournalStorageProvider : IJournalStorageProvider, IJournalStorageCatalog, ILifecycleParticipant<ISiloLifecycle>, IAsyncDisposable
{
    private const int ConcurrentBucketReads = 32;
    private static readonly PartitionId DefaultPartitionId = new("p0");

    private readonly GarnetJournalStorageOptions _options;
    private readonly string _journalFormatKey;
    private readonly JournalStorageTelemetry _telemetry;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<GarnetJournalStorageProvider>? _logger;
    private GarnetJournalPartitionManager? _partitions;
    private GarnetJournalRouter? _router;

    public GarnetJournalStorageProvider(
        IOptions<GarnetJournalStorageOptions> options,
        IOptions<JournaledStateManagerOptions> managerOptions,
        ILoggerFactory? loggerFactory = null,
        OrleansInstruments? instruments = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(managerOptions);
        _options = options.Value;
        _journalFormatKey = JournalFormatServices.ValidateJournalFormatKey(managerOptions.Value.JournalFormatKey);
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<GarnetJournalStorageProvider>();
        _telemetry = instruments is null ? JournalStorageTelemetry.CreateForDirectConstruction() : new JournalStorageTelemetry(instruments);
    }

    /// <summary>Gets the partitions this silo hosts.</summary>
    internal IReadOnlyDictionary<PartitionId, GarnetJournalPartitionHost> HostedPartitions
        => _partitions?.Hosts ?? new Dictionary<PartitionId, GarnetJournalPartitionHost>();

    internal GarnetJournalRouter Router => _router ?? throw new InvalidOperationException(
        $"{nameof(GarnetJournalStorageProvider)} has not been initialized. Ensure the silo lifecycle has started before using journal storage.");

    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        return new GarnetJournalStorage(Router, journalId, _journalFormatKey, _options.CompactionThresholdBytes);
    }

    /// <remarks>
    /// Reads each bucket's index from the partition which owns it, as one snapshot per bucket. The index is updated in
    /// the same transaction as every create and delete, so a listing includes every journal which exists for its
    /// whole duration.
    /// </remarks>
    public async IAsyncEnumerable<JournalCatalogEntry> ListAsync(
        JournalCatalogListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var range = new JournalCatalogRange(options);
        if (range.IsEmpty)
        {
            yield break;
        }

        var router = Router;
        for (var start = 0; start < router.BucketCount; start += ConcurrentBucketReads)
        {
            var count = Math.Min(ConcurrentBucketReads, router.BucketCount - start);
            var reads = new Task<RedisResult[]>[count];
            for (var i = 0; i < count; i++)
            {
                var bucket = start + i;
                reads[i] = router.ExecuteAsync(
                    bucket,
                    GarnetJournalProtocol.Index,
                    [(RedisKey)GarnetJournalKeys.GetBucketKey(bucket), (RedisKey)GarnetJournalKeys.GetIndexKey(bucket)],
                    cancellationToken).AsTask();
            }

            var results = await Task.WhenAll(reads).ConfigureAwait(false);
            for (var i = 0; i < results.Length; i++)
            {
                var members = results[i];
                if (GarnetJournalRouter.GetStatus(members) != GarnetJournalProtocol.SuccessStatus)
                {
                    throw new InvalidOperationException(
                        $"Garnet journal bucket {start + i} has an incomplete migration, which this version cannot list.");
                }

                _telemetry.OnCatalogPage(GarnetJournalRouter.ProviderName, members.Length - 1);
                for (var m = 1; m < members.Length; m++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!GarnetJournalKeys.TryDecodeJournalId((byte[])members[m]!, out var journalId))
                    {
                        throw new InvalidOperationException("A Garnet journal bucket index contains an invalid journal id.");
                    }

                    if (range.Contains(journalId.Value))
                    {
                        _telemetry.OnCatalogEntry(GarnetJournalRouter.ProviderName);
                        yield return new JournalCatalogEntry(journalId);
                    }
                }
            }
        }
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(nameof(GarnetJournalStorageProvider), _options.InitStage, onStart: StartAsync, onStop: StopAsync);
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metaStore = _options.MetaStore
            ?? throw new InvalidOperationException($"{nameof(GarnetJournalStorageOptions)}.{nameof(GarnetJournalStorageOptions.MetaStore)} is required.");
        var hostId = _options.HostId is { Length: > 0 } id
            ? id
            : throw new InvalidOperationException($"{nameof(GarnetJournalStorageOptions)}.{nameof(GarnetJournalStorageOptions.HostId)} is required.");
        var view = await GetRangeMapAsync(metaStore, hostId, cancellationToken).ConfigureAwait(false);
        _partitions = await GarnetJournalPartitionManager.StartAsync(_options, hostId, metaStore, view, _loggerFactory, cancellationToken).ConfigureAwait(false);
        _router = new GarnetJournalRouter(metaStore, _partitions.View, _options.OperationTimeout, _telemetry, _logger);
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_router is { } router)
        {
            _router = null;
            await router.DisposeAsync().ConfigureAwait(false);
        }

        _partitions?.Dispose();
        _partitions = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Reads the range map, writing the initial map to an empty MetaStore, or waiting for another silo to write it.
    /// </summary>
    private async Task<RangeMapSnapshot> GetRangeMapAsync(IRangeMetaStore metaStore, string hostId, CancellationToken cancellationToken)
    {
        var initial = _options.InitialRangeMap;
        if (initial is null && !string.IsNullOrWhiteSpace(_options.DataDirectory))
        {
            initial = RangeMap.CreateUniform(
                _options.BucketCount,
                [new(DefaultPartitionId, new PartitionHosting(hostId, Epoch: 0, StorageLocation: DefaultPartitionId.Value))]);
        }

        var delay = TimeSpan.FromMilliseconds(100);
        while (true)
        {
            var snapshot = await metaStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (snapshot is null && initial is not null)
            {
                snapshot = (await metaStore.TryWriteAsync(initial, expectedVersion: 0, cancellationToken).ConfigureAwait(false)).Current;
            }

            if (snapshot is not null)
            {
                snapshot.Map.Validate();
                return snapshot;
            }

            _logger?.LogInformation("Waiting for another silo to write the Garnet journal range map.");
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(5).Ticks));
        }
    }
}
