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
    private const string HostIdFileName = "host-id";
    private static readonly PartitionId DefaultPartitionId = new("p0");

    private readonly GarnetJournalStorageOptions _options;
    private readonly string _journalFormatKey;
    private readonly JournalStorageTelemetry _telemetry;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<GarnetJournalStorageProvider>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ILocalSiloDetails? _siloDetails;
    private readonly IClusterMembershipService? _membership;
    private GarnetJournalPartitionManager? _partitions;
    private GarnetJournalRouter? _router;
    private GarnetJournalMigrationCoordinator? _coordinator;
    private GarnetJournalMigrationAgent? _agent;
    private GarnetJournalFailoverAgent? _failoverAgent;

    public GarnetJournalStorageProvider(
        IOptions<GarnetJournalStorageOptions> options,
        IOptions<JournaledStateManagerOptions> managerOptions,
        ILoggerFactory? loggerFactory = null,
        OrleansInstruments? instruments = null,
        TimeProvider? timeProvider = null,
        ILocalSiloDetails? siloDetails = null,
        IClusterMembershipService? membership = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _siloDetails = siloDetails;
        _membership = membership;
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

    /// <summary>Gets the coordinator which commits and drives migrations.</summary>
    internal GarnetJournalMigrationCoordinator Coordinator => _coordinator ?? throw new InvalidOperationException(
        $"{nameof(GarnetJournalStorageProvider)} has not been initialized. Ensure the silo lifecycle has started before using journal storage.");

    /// <summary>Gets the agent which drives migrations into this silo's partitions, if it can host any.</summary>
    internal GarnetJournalMigrationAgent? MigrationAgent => _agent;

    /// <summary>Gets the manager of the partitions this silo hosts.</summary>
    internal GarnetJournalPartitionManager PartitionManager => _partitions ?? throw new InvalidOperationException(
        $"{nameof(GarnetJournalStorageProvider)} has not been initialized. Ensure the silo lifecycle has started before using journal storage.");

    /// <summary>Gets the agent which fails partitions over from dead silos, if this silo runs one.</summary>
    internal GarnetJournalFailoverAgent? FailoverAgent => _failoverAgent;

    /// <summary>Gets the identifier of this silo in the range map's hosting assignments, if it can host partitions.</summary>
    internal string? HostId { get; private set; }

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
    /// whole duration. A bucket whose migration is incomplete is consolidated first, so its index lists every journal
    /// on its migration chain.
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
                reads[i] = ReadIndexAsync(router, start + i, cancellationToken);
            }

            var results = await Task.WhenAll(reads).ConfigureAwait(false);
            for (var i = 0; i < results.Length; i++)
            {
                var members = results[i];
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

    /// <summary>Reads a bucket's index at its owner, consolidating the bucket first while its migration is incomplete.</summary>
    private static async Task<RedisResult[]> ReadIndexAsync(GarnetJournalRouter router, int bucket, CancellationToken cancellationToken)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            var result = await router.ExecuteAsync(
                bucket,
                GarnetJournalProtocol.Index,
                [(RedisKey)GarnetJournalKeys.GetBucketKey(router.MapId, bucket), (RedisKey)GarnetJournalKeys.GetIndexKey(bucket)],
                cancellationToken).ConfigureAwait(false);
            var status = GarnetJournalRouter.GetStatus(result);
            if (status == GarnetJournalProtocol.SuccessStatus)
            {
                return result;
            }

            if (status != GarnetJournalProtocol.NeedPullStatus)
            {
                throw new InvalidOperationException($"Listing Garnet journal bucket {bucket} failed with status {status}.");
            }

            var (consolidation, _) = await router.Migrator.ConsolidateAsync(bucket, router.View.Map.GetOwner(bucket), cancellationToken).ConfigureAwait(false);
            if (consolidation != ConsolidationResult.Done)
            {
                if (System.Diagnostics.Stopwatch.GetElapsedTime(start) >= router.OperationTimeout)
                {
                    throw new TimeoutException($"Garnet journal bucket {bucket} could not be consolidated for listing within {router.OperationTimeout}.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
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
        var shared = _options.PartitionBlobContainer is not null;
        var hostId = _options.HostId
            ?? (shared ? _siloDetails?.SiloAddress.ToParsableString()
                : string.IsNullOrWhiteSpace(_options.DataDirectory) ? null : GetOrCreateHostId(_options.DataDirectory));
        var canHost = hostId is not null && (shared || !string.IsNullOrWhiteSpace(_options.DataDirectory));
        HostId = canHost ? hostId : null;
        var view = await GetRangeMapAsync(metaStore, HostId, cancellationToken).ConfigureAwait(false);
        _partitions = await GarnetJournalPartitionManager.StartAsync(_options, hostId, metaStore, view, _loggerFactory, cancellationToken).ConfigureAwait(false);
        var router = _router = new GarnetJournalRouter(metaStore, _partitions.View, _options.OperationTimeout, _telemetry, _logger, _timeProvider);
        _partitions.ViewObserved = view => router.TryApplyView(view);
        _partitions.StartMonitoring();
        _coordinator = new GarnetJournalMigrationCoordinator(_router, _loggerFactory?.CreateLogger<GarnetJournalMigrationCoordinator>());
        if (canHost)
        {
            _agent = new GarnetJournalMigrationAgent(
                _router,
                _coordinator,
                _partitions.IsHosted,
                _options,
                _loggerFactory?.CreateLogger<GarnetJournalMigrationAgent>());
            _agent.Start();
        }

        // Silos identified by their addresses fail partitions over from silos which membership declares dead.
        if (shared && _options.HostId is null && _membership is not null)
        {
            _failoverAgent = new GarnetJournalFailoverAgent(
                new GarnetJournalHostingCoordinator(metaStore, _loggerFactory?.CreateLogger<GarnetJournalHostingCoordinator>()),
                _membership,
                _options.HostingInterval,
                _loggerFactory?.CreateLogger<GarnetJournalFailoverAgent>());
            _failoverAgent.Start();
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_failoverAgent is { } failoverAgent)
        {
            _failoverAgent = null;
            await failoverAgent.DisposeAsync().ConfigureAwait(false);
        }

        if (_agent is { } agent)
        {
            _agent = null;
            await agent.DisposeAsync().ConfigureAwait(false);
        }

        _coordinator = null;
        if (_partitions is { } partitions)
        {
            _partitions = null;
            await partitions.DisposeAsync().ConfigureAwait(false);
        }

        if (_router is { } router)
        {
            _router = null;
            await router.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Reads this host's identifier from <paramref name="dataDirectory"/>, generating and storing one on first use, so
    /// the identifier is stable for as long as the directory, which holds the host's partitions, persists.
    /// </summary>
    internal static string GetOrCreateHostId(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, HostIdFileName);
        if (!File.Exists(path))
        {
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    stream.Write(System.Text.Encoding.ASCII.GetBytes($"host-{Guid.NewGuid():N}"));
                    stream.Flush(flushToDisk: true);
                }

                // Another silo sharing the directory may have stored an identifier first; keep that one.
                File.Move(temporary, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        var hostId = File.ReadAllText(path).Trim();
        return hostId.Length > 0
            ? hostId
            : throw new InvalidOperationException($"The Garnet journal host identifier file '{path}' is empty.");
    }

    /// <summary>
    /// Reads the range map, writing the initial map to an empty MetaStore, or waiting for another silo to write it.
    /// </summary>
    private async Task<RangeMapSnapshot> GetRangeMapAsync(IRangeMetaStore metaStore, string? hostId, CancellationToken cancellationToken)
    {
        var initial = _options.InitialRangeMap;
        if (initial is null && hostId is not null)
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
                try
                {
                    snapshot = (await metaStore.TryWriteAsync(initial, expectedVersion: 0, cancellationToken).ConfigureAwait(false)).Current;
                }
                catch (IOException exception)
                {
                    // The write's outcome is unknown: read the map again.
                    _logger?.LogWarning(exception, "The outcome of writing the initial Garnet journal range map is unknown.");
                    continue;
                }
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
