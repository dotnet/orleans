using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

internal sealed partial class JournaledJobShardManager : JobShardManager
{
    private const string OwnerProperty = "DurableJobsOwner";
    private const string MembershipVersionProperty = "DurableJobsMembershipVersion";
    private const string MinDueTimeProperty = "DurableJobsMinDueTime";
    private const string MaxDueTimeProperty = "DurableJobsMaxDueTime";
    private const string AdoptedCountProperty = "DurableJobsAdoptedCount";
    private const string LastAdoptedTimeProperty = "DurableJobsLastAdoptedTime";
    private const string PoisonedProperty = "DurableJobsPoisoned";
    private const string ClosedProperty = "DurableJobsClosed";
    private const string MetadataPropertyPrefix = "DurableJobsMetadata_";

    private readonly DurableJobsJournalProviders _providers;
    private readonly ILogger<JournaledJobShardManager> _logger;
    private readonly IClusterMembershipService _membershipService;
    private readonly IServiceProvider _serviceProvider;
    private readonly DurableJobsInstruments _durableJobsInstruments;
    private readonly DurableJobsOptions _options;
    private readonly JournaledStateManagerOptions _journaledStateManagerOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, JournaledJobShard> _jobShardCache = new();
    // Sticky positive cache for IsShardOwnedByLocalSiloAsync. Entries are added once a shard is
    // confirmed to be owned by this silo, and removed only when ownership is released locally
    // (via UnregisterShardAsync). Mis-cache from split-brain is bounded by storage-layer ETag
    // conflicts triggering InconsistentStateException → the journaling layer's recovery path.
    private readonly ConcurrentDictionary<string, bool> _ownedShards = new(StringComparer.Ordinal);
    public JournaledJobShardManager(
        ILocalSiloDetails localSiloDetails,
        IJournaledStateManagerFactory stateManagerFactory,
        IJournalStorageProvider storageProvider,
        IJournalStorageCatalog catalog,
        IClusterMembershipService membershipService,
        IServiceProvider serviceProvider,
        IOptions<DurableJobsOptions> options,
        IOptions<JournaledStateManagerOptions> journaledStateManagerOptions,
        DurableJobsInstruments? durableJobsInstruments = null)
        : this(
            localSiloDetails,
            new DurableJobsJournalProviders(new DurableJobsJournalProvider(
                ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME,
                storageProvider ?? throw new ArgumentNullException(nameof(storageProvider)),
                catalog ?? throw new ArgumentNullException(nameof(catalog)),
                stateManagerFactory ?? throw new ArgumentNullException(nameof(stateManagerFactory)))),
            membershipService,
            serviceProvider,
            options,
            journaledStateManagerOptions,
            durableJobsInstruments)
    {
    }

    public JournaledJobShardManager(
        ILocalSiloDetails localSiloDetails,
        DurableJobsJournalProviders providers,
        IClusterMembershipService membershipService,
        IServiceProvider serviceProvider,
        IOptions<DurableJobsOptions> options,
        IOptions<JournaledStateManagerOptions> journaledStateManagerOptions,
        DurableJobsInstruments? durableJobsInstruments = null,
        ILogger<JournaledJobShardManager>? logger = null)
        : base(GetSiloAddress(localSiloDetails))
    {
        ArgumentNullException.ThrowIfNull(localSiloDetails);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(membershipService);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(journaledStateManagerOptions);

        _providers = providers;
        _logger = logger ?? NullLogger<JournaledJobShardManager>.Instance;
        _membershipService = membershipService;
        _serviceProvider = serviceProvider;
        _durableJobsInstruments = durableJobsInstruments ?? DurableJobsInstruments.CreateForDirectConstruction();
        _options = options.Value;
        _journaledStateManagerOptions = journaledStateManagerOptions.Value;
        _timeProvider = serviceProvider.GetKeyedService<TimeProvider>(DurableJobTimeProviderNames.DurableJobs) ?? TimeProvider.System;
    }

    internal DurableJobsJournalProvider WriteProvider => _providers.WriteProvider;

    private static SiloAddress GetSiloAddress(ILocalSiloDetails localSiloDetails)
    {
        ArgumentNullException.ThrowIfNull(localSiloDetails);
        return localSiloDetails.SiloAddress;
    }

    public override async Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxDueTime, int maxNewClaims, CancellationToken cancellationToken)
    {
        var result = new List<IJobShard>();
        await foreach (var shard in DiscoverJobShardsAsync(maxDueTime, maxNewClaims, cancellationToken))
        {
            result.Add(shard);
        }

        return result;
    }

    internal override async IAsyncEnumerable<IJobShard> DiscoverJobShardsAsync(
        DateTimeOffset maxDueTime,
        int maxNewClaims,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var storageEntries = new SortedDictionary<JournalId, (DurableJobsJournalProvider Provider, JournalCatalogEntry Entry)>(Comparer<JournalId>.Create(
            static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value)));
        var options = new JournalCatalogListOptions
        {
            Prefix = new JournalId(JobShardId.StoragePrefix.Value + "/"),
            MaxId = JobShardId.GetMaxJournalId(maxDueTime),
            IncludeMetadata = true
        };
        foreach (var provider in _providers.Providers)
        {
            // Publish a provider's candidates only after its enumeration completes successfully.
            var entries = _providers.Providers.Length == 1
                ? storageEntries
                : new SortedDictionary<JournalId, (DurableJobsJournalProvider Provider, JournalCatalogEntry Entry)>(storageEntries.Comparer);
            try
            {
                await foreach (var entry in provider.Catalog.ListAsync(options, cancellationToken))
                {
                    entries.TryAdd(entry.Id, (provider, entry));
                }
            }
            catch (Exception exception) when (_providers.Providers.Length > 1
                && (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
            {
                LogProviderFailure(_logger, exception, provider.Name, "catalog discovery", options.Prefix.Value);
                continue;
            }

            if (!ReferenceEquals(entries, storageEntries))
            {
                foreach (var entry in entries)
                {
                    storageEntries.TryAdd(entry.Key, entry.Value);
                }
            }
        }

        // Providers can return identities in any order. Names order the selected shards by UTC start time.
        var newClaimCount = 0;
        HashSet<DurableJobsJournalProvider>? failedProviders = null;
        foreach (var (provider, entry) in storageEntries.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failedProviders?.Contains(provider) == true)
            {
                continue;
            }

            IJobShard? shard;
            bool claimed;
            try
            {
                (shard, claimed) = await TryAssignShardAsync(provider, entry, maxDueTime, newClaimCount < maxNewClaims, cancellationToken);
            }
            catch (Exception exception) when (_providers.Providers.Length > 1
                && (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
            {
                LogProviderFailure(_logger, exception, provider.Name, "shard assignment", entry.Id.Value);
                (failedProviders ??= []).Add(provider);
                continue;
            }
            if (claimed)
            {
                newClaimCount++;
            }
            if (shard is not null)
            {
                yield return shard;
            }
        }
    }

    private async ValueTask<(IJobShard? Shard, bool Claimed)> TryAssignShardAsync(
        DurableJobsJournalProvider provider, JournalCatalogEntry entry, DateTimeOffset maxDueTime, bool canClaim, CancellationToken cancellationToken)
    {
        var descriptor = entry.Metadata is { ETag: not null } metadata
            ? ShardCatalogProperties.From(provider, entry.Id, metadata)
            : await GetDescriptorAsync(provider, entry.Id, cancellationToken);
        JournaledJobShard? cachedShard = null;
        if (descriptor?.Owner is { } snapshotOwner && snapshotOwner.Equals(SiloAddress)
            && !_jobShardCache.TryGetValue(descriptor.ShardId.Value, out cachedShard)
            && entry.Metadata is { ETag: not null })
        {
            // A listed local owner can have released the shard since the snapshot was taken.
            descriptor = await GetDescriptorAsync(provider, entry.Id, cancellationToken);
        }

        if (descriptor is null || descriptor.Poisoned || descriptor.StartTime > maxDueTime)
        {
            return default;
        }

        var membershipSnapshot = _membershipService.CurrentSnapshot;
        if (descriptor.MembershipVersion > membershipSnapshot.Version)
        {
            await _membershipService.Refresh(descriptor.MembershipVersion, cancellationToken);
            membershipSnapshot = _membershipService.CurrentSnapshot;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (descriptor.Owner is { } owner && owner.Equals(SiloAddress))
        {
            if (!ReferenceEquals(provider, WriteProvider) && !descriptor.Closed)
            {
                var updated = await UpdateMetadataAsync(
                    descriptor,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [ClosedProperty] = bool.TrueString },
                    remove: null,
                    cancellationToken);
                if (updated is null)
                {
                    return default;
                }

                descriptor = ShardCatalogProperties.From(provider, entry.Id, updated)!;
            }

            return (cachedShard ?? await GetOrOpenShardAsync(descriptor, cancellationToken), false);
        }

        var isAdopted = false;
        if (descriptor.Owner is { } previousOwner)
        {
            var ownerStatus = membershipSnapshot.GetSiloStatus(previousOwner);
            if (ownerStatus is not SiloStatus.Dead and not SiloStatus.None)
            {
                return default;
            }

            isAdopted = ownerStatus == SiloStatus.Dead;
        }

        // Exhausting the claim budget still advances discovery to later locally owned shards.
        if (!canClaim)
        {
            return default;
        }

        var claimedShard = await TryClaimShardAsync(descriptor, isAdopted, cancellationToken);
        if (claimedShard is null)
        {
            return default;
        }

        if (_jobShardCache.TryAdd(claimedShard.Id, claimedShard))
        {
            return (claimedShard, true);
        }

        // Unregister can still own the previous instance after releasing storage ownership.
        await claimedShard.DisposeAsync();
        return (null, true);
    }

    public override async Task<IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        while (true)
        {
            var shardId = JobShardId.New(minDueTime);
            var storageId = shardId.ToJournalId();
            var initialProperties = CreateInitialProperties(minDueTime, maxDueTime, metadata);
            var storage = WriteProvider.Storage.CreateStorage(storageId);
            if (!await storage.CreateIfNotExistsAsync(initialProperties, cancellationToken))
            {
                continue;
            }

            var properties = await storage.GetMetadataAsync(cancellationToken);
            var descriptor = properties is not null ? ShardCatalogProperties.From(WriteProvider, storageId, properties) : null;
            if (descriptor is null)
            {
                throw new InvalidOperationException($"Created DurableJobs shard '{shardId}' without readable journal storage properties.");
            }

            var shard = await OpenShardAsync(descriptor, cancellationToken);
            _jobShardCache[shard.Id] = shard;
            return shard;
        }
    }

    public override async Task UnregisterShardAsync(IJobShard shard, CancellationToken cancellationToken)
    {
        var journaledShard = shard as JournaledJobShard
            ?? throw new ArgumentException("Shard is not a journaled DurableJobs shard.", nameof(shard));

        try
        {
            var descriptor = await GetDescriptorAsync(journaledShard.Provider, journaledShard.StorageId, cancellationToken)
                ?? throw new InvalidOperationException($"Cannot unregister DurableJobs shard '{shard.Id}' because its catalog properties were not found.");

            if (descriptor.Owner is null || !descriptor.Owner.Equals(SiloAddress))
            {
                throw new InvalidOperationException("Cannot unregister a DurableJobs shard owned by another silo.");
            }

            var count = await shard.GetJobCountAsync();
            if (count == 0)
            {
                // No jobs left, we can delete the shard.
                await journaledShard.DeleteStateAsync(cancellationToken);
            }
            else
            {
                // There are still jobs in the shard, release ownership gracefully.
                var updatedMetadata = await UpdateMetadataAsync(
                    descriptor,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [ClosedProperty] = bool.TrueString,
                        [MembershipVersionProperty] = GetMembershipVersionString()
                    },
                    [OwnerProperty, AdoptedCountProperty, LastAdoptedTimeProperty],
                    cancellationToken);

                if (updatedMetadata is null)
                {
                    throw new InvalidOperationException($"Failed to release DurableJobs shard '{shard.Id}' ownership.");
                }
            }
        }
        finally
        {
            _jobShardCache.TryRemove(shard.Id, out _);
            _ownedShards.TryRemove(shard.Id, out _);
            await journaledShard.DisposeAsync();
        }
    }

    internal override async ValueTask<SiloAddress?> GetShardOwnerAsync(string shardId, CancellationToken cancellationToken)
    {
        var descriptor = await GetDescriptorAsync(shardId, cancellationToken);
        if (descriptor is null || descriptor.Poisoned || descriptor.Owner is null)
        {
            return null;
        }

        if (descriptor.Owner.Equals(SiloAddress))
        {
            return descriptor.Owner;
        }

        var membershipSnapshot = _membershipService.CurrentSnapshot;
        if (descriptor.MembershipVersion > membershipSnapshot.Version)
        {
            await _membershipService.Refresh(descriptor.MembershipVersion, cancellationToken);
            membershipSnapshot = _membershipService.CurrentSnapshot;
        }

        return membershipSnapshot.GetSiloStatus(descriptor.Owner) == SiloStatus.Active ? descriptor.Owner : null;
    }

    internal override async ValueTask<bool> IsShardOwnedByLocalSiloAsync(string shardId, CancellationToken cancellationToken)
    {
        if (_ownedShards.ContainsKey(shardId))
        {
            return true;
        }

        var descriptor = await GetDescriptorAsync(shardId, cancellationToken);
        return CacheOwnership(shardId, descriptor);
    }

    internal async ValueTask<bool> IsShardOwnedByLocalSiloAsync(DurableJobsJournalProvider provider, string shardId, CancellationToken cancellationToken)
    {
        if (_ownedShards.ContainsKey(shardId))
        {
            return true;
        }

        var descriptor = await GetDescriptorAsync(provider, JobShardId.Parse(shardId).ToJournalId(), cancellationToken);
        return CacheOwnership(shardId, descriptor);
    }

    private bool CacheOwnership(string shardId, ShardCatalogProperties? descriptor)
    {
        var isOwned = descriptor is { Poisoned: false, Owner: { } owner } && owner.Equals(SiloAddress);
        if (isOwned)
        {
            _ownedShards[shardId] = true;
        }

        return isOwned;
    }

    internal async ValueTask<bool> TryMarkShardClosedAsync(DurableJobsJournalProvider provider, string shardId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var descriptor = await GetDescriptorAsync(provider, JobShardId.Parse(shardId).ToJournalId(), cancellationToken);
            if (descriptor is null || descriptor.Poisoned || descriptor.Owner is null || !descriptor.Owner.Equals(SiloAddress))
            {
                return false;
            }

            if (descriptor.Closed)
            {
                return true;
            }

            var result = await UpdateMetadataAsync(
                descriptor,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ClosedProperty] = bool.TrueString,
                    [MembershipVersionProperty] = GetMembershipVersionString()
                },
                remove: null,
                cancellationToken);
            if (result is not null)
            {
                return true;
            }
        }

        return false;
    }

    private async ValueTask<JournaledJobShard?> TryClaimShardAsync(ShardCatalogProperties descriptor, bool isAdopted, CancellationToken cancellationToken)
    {
        var adoptedCount = descriptor.AdoptedCount;
        var set = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OwnerProperty] = SiloAddress.ToParsableString(),
            [MembershipVersionProperty] = GetMembershipVersionString(),
            // We don't want to add new jobs to shards that we just took ownership of.
            [ClosedProperty] = bool.TrueString
        };
        List<string>? remove = null;

        if (isAdopted)
        {
            // Increment adopted count for shards taken from dead owners.
            adoptedCount++;
            if (adoptedCount > _options.MaxAdoptedCount)
            {
                // Persist poisoned marker so this shard is not repeatedly re-evaluated as newly poisoned.
                await TryMarkShardPoisonedAsync(descriptor, adoptedCount, cancellationToken);
                return null;
            }

            set[AdoptedCountProperty] = adoptedCount.ToString(CultureInfo.InvariantCulture);
            set[LastAdoptedTimeProperty] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        }
        else
        {
            // Reset adopted count since we're gracefully releasing.
            set[AdoptedCountProperty] = "0";
            remove = [LastAdoptedTimeProperty];
        }

        var updatedMetadata = await UpdateMetadataAsync(descriptor, set, remove, cancellationToken);
        if (updatedMetadata is null)
        {
            return null;
        }

        var updatedDescriptor = ShardCatalogProperties.From(descriptor.Provider, descriptor.StorageId, updatedMetadata);
        return updatedDescriptor is null || updatedDescriptor.Owner is null || !updatedDescriptor.Owner.Equals(SiloAddress)
            ? null
            : await OpenShardAsync(updatedDescriptor, cancellationToken);
    }

    private async Task TryMarkShardPoisonedAsync(ShardCatalogProperties descriptor, int adoptedCount, CancellationToken cancellationToken)
    {
        await UpdateMetadataAsync(
            descriptor,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PoisonedProperty] = bool.TrueString,
                [AdoptedCountProperty] = adoptedCount.ToString(CultureInfo.InvariantCulture),
                [LastAdoptedTimeProperty] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                [MembershipVersionProperty] = GetMembershipVersionString()
            },
            remove: null,
            cancellationToken);
    }

    private async ValueTask<JournaledJobShard> GetOrOpenShardAsync(ShardCatalogProperties descriptor, CancellationToken cancellationToken)
    {
        if (_jobShardCache.TryGetValue(descriptor.ShardId.Value, out var existing))
        {
            return existing;
        }

        var shard = await OpenShardAsync(descriptor, cancellationToken);
        if (_jobShardCache.TryAdd(shard.Id, shard))
        {
            return shard;
        }

        await shard.DisposeAsync();
        return _jobShardCache[descriptor.ShardId.Value];
    }

    private async ValueTask<JournaledJobShard> OpenShardAsync(ShardCatalogProperties descriptor, CancellationToken cancellationToken)
    {
        var codec = CreateOperationCodec();
        var state = new JournaledJobShardState(descriptor.ShardId, descriptor.StartTime, descriptor.EndTime, codec, _timeProvider);
        var manager = descriptor.Provider.Factory.CreateStandalone(descriptor.StorageId);
        try
        {
            manager.RegisterStateMachine(JournaledJobShardState.StateName, state);
            await manager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await manager.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // Pre-populate the ownership cache when the descriptor confirms this silo owns the shard,
        // so the first batch flush does not pay an Azure metadata round trip.
        if (descriptor is { Poisoned: false, Owner: { } owner } && owner.Equals(SiloAddress))
        {
            _ownedShards[descriptor.ShardId.Value] = true;
        }

        return new JournaledJobShard(
            descriptor.ShardId,
            descriptor.Provider,
            descriptor.StartTime,
            descriptor.EndTime,
            descriptor.Metadata,
            descriptor.Closed,
            state,
            manager,
            this,
            _timeProvider,
            _options.ShardBatchLingerDelay,
            _durableJobsInstruments);
    }

    private IDurableValueCommandCodec<DurableJobShardJournalRecord> CreateOperationCodec()
    {
        var journalFormatKey = _journaledStateManagerOptions.JournalFormatKey;
        if (string.IsNullOrWhiteSpace(journalFormatKey))
        {
            throw new InvalidOperationException("The configured journal format key must be non-empty.");
        }

        var codec = _serviceProvider.GetKeyedService<IDurableValueCommandCodec<DurableJobShardJournalRecord>>(journalFormatKey);
        return codec ?? throw new InvalidOperationException(
            $"Journal format key '{journalFormatKey}' requires keyed service '{typeof(IDurableValueCommandCodec<DurableJobShardJournalRecord>).FullName}', but none was registered.");
    }

    private async ValueTask<ShardCatalogProperties?> GetDescriptorAsync(string shardId, CancellationToken cancellationToken)
    {
        JournalId storageId;
        try
        {
            storageId = JobShardId.Parse(shardId).ToJournalId();
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (_providers.Providers.Length == 1)
        {
            return await GetDescriptorAsync(WriteProvider, storageId, cancellationToken, requireValidMetadata: true);
        }

        if (_jobShardCache.TryGetValue(shardId, out var shard))
        {
            return await GetDescriptorAsync(shard.Provider, storageId, cancellationToken, requireValidMetadata: true);
        }

        List<Exception>? failures = null;
        foreach (var provider in _providers.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var descriptor = await GetDescriptorAsync(provider, storageId, cancellationToken, requireValidMetadata: true);
                if (descriptor is not null)
                {
                    return descriptor;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogProviderFailure(_logger, exception, provider.Name, "shard lookup", storageId.Value);
                (failures ??= []).Add(new InvalidOperationException(
                    $"Durable Jobs provider '{provider.Name}' could not locate shard '{shardId}'.", exception));
            }
        }

        if (failures is not null)
        {
            throw new AggregateException($"The location of Durable Jobs shard '{shardId}' could not be determined.", failures);
        }

        return null;
    }

    private static async ValueTask<ShardCatalogProperties?> GetDescriptorAsync(
        DurableJobsJournalProvider provider, JournalId storageId, CancellationToken cancellationToken, bool requireValidMetadata = false)
    {
        var properties = await provider.Storage.CreateStorage(storageId).GetMetadataAsync(cancellationToken);
        if (properties is null)
        {
            return null;
        }

        var result = ShardCatalogProperties.From(provider, storageId, properties);
        if (result is null && requireValidMetadata)
        {
            throw new InvalidOperationException($"Durable Jobs journal '{storageId}' in provider '{provider.Name}' has unrecognized shard metadata.");
        }

        return result;
    }

    private async ValueTask<IJournalMetadata?> UpdateMetadataAsync(
        ShardCatalogProperties descriptor,
        IReadOnlyDictionary<string, string>? set,
        IEnumerable<string>? remove,
        CancellationToken cancellationToken)
    {
        var expectedETag = descriptor.Properties.ETag
            ?? throw new InvalidOperationException($"DurableJobs shard '{descriptor.ShardId}' requires a storage metadata ETag for conditional ownership updates.");
        var storage = descriptor.Provider.Storage.CreateStorage(descriptor.StorageId);
        return await storage.UpdateMetadataAsync(set, remove, expectedETag, cancellationToken);
    }

    private Dictionary<string, string> CreateInitialProperties(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string>? metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OwnerProperty] = SiloAddress.ToParsableString(),
            [MembershipVersionProperty] = GetMembershipVersionString(),
            [MinDueTimeProperty] = minDueTime.ToString("O", CultureInfo.InvariantCulture),
            [MaxDueTimeProperty] = maxDueTime.ToString("O", CultureInfo.InvariantCulture),
            [AdoptedCountProperty] = "0",
            [ClosedProperty] = bool.FalseString
        };

        if (metadata is not null)
        {
            foreach (var (key, value) in metadata)
            {
                result[MetadataPropertyPrefix + EncodeMetadataKey(key)] = value;
            }
        }

        return result;
    }

    private string GetMembershipVersionString() => _membershipService.CurrentSnapshot.Version.Value.ToString(CultureInfo.InvariantCulture);

    private static string EncodeMetadataKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string DecodeMetadataKey(string encoded)
    {
        var base64 = encoded.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Durable Jobs provider '{ProviderName}' failed during {Operation} for journal '{JournalId}'.")]
    private static partial void LogProviderFailure(ILogger logger, Exception exception, string providerName, string operation, string journalId);

    internal sealed class ShardCatalogProperties
    {
        private ShardCatalogProperties(
            DurableJobsJournalProvider provider,
            JournalId storageId,
            JobShardId shardId,
            IJournalMetadata properties,
            SiloAddress? owner,
            MembershipVersion membershipVersion,
            DateTimeOffset startTime,
            DateTimeOffset endTime,
            int adoptedCount,
            bool poisoned,
            bool closed,
            IReadOnlyDictionary<string, string> metadata)
        {
            Provider = provider;
            StorageId = storageId;
            ShardId = shardId;
            Properties = properties;
            Owner = owner;
            MembershipVersion = membershipVersion;
            StartTime = startTime;
            EndTime = endTime;
            AdoptedCount = adoptedCount;
            Poisoned = poisoned;
            Closed = closed;
            Metadata = metadata;
        }

        public DurableJobsJournalProvider Provider { get; }

        public JournalId StorageId { get; }

        public JobShardId ShardId { get; }

        public IJournalMetadata Properties { get; }

        public SiloAddress? Owner { get; }

        public MembershipVersion MembershipVersion { get; }

        public DateTimeOffset StartTime { get; }

        public DateTimeOffset EndTime { get; }

        public int AdoptedCount { get; }

        public bool Poisoned { get; }

        public bool Closed { get; }

        public IReadOnlyDictionary<string, string> Metadata { get; }

        public static ShardCatalogProperties? From(DurableJobsJournalProvider provider, JournalId storageId, IJournalMetadata properties)
        {
            try
            {
                var values = properties.Properties;
                if (!values.TryGetValue(MinDueTimeProperty, out var minDueTimeValue)
                    || !DateTimeOffset.TryParse(minDueTimeValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var minDueTime)
                    || !values.TryGetValue(MaxDueTimeProperty, out var maxDueTimeValue)
                    || !DateTimeOffset.TryParse(maxDueTimeValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var maxDueTime))
                {
                    return null;
                }

                var owner = values.TryGetValue(OwnerProperty, out var ownerValue) && !string.IsNullOrWhiteSpace(ownerValue)
                    ? SiloAddress.FromParsableString(ownerValue)
                    : null;

                var membershipVersion = values.TryGetValue(MembershipVersionProperty, out var membershipVersionValue)
                    && long.TryParse(membershipVersionValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMembershipVersion)
                        ? new MembershipVersion(parsedMembershipVersion)
                        : MembershipVersion.MinValue;

                var adoptedCount = values.TryGetValue(AdoptedCountProperty, out var adoptedCountValue)
                    && int.TryParse(adoptedCountValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAdoptedCount)
                        ? parsedAdoptedCount
                        : 0;

                var poisoned = values.TryGetValue(PoisonedProperty, out var poisonedValue)
                    && bool.TryParse(poisonedValue, out var parsedPoisoned)
                    && parsedPoisoned;

                var closed = values.TryGetValue(ClosedProperty, out var closedValue)
                    && bool.TryParse(closedValue, out var parsedClosed)
                    && parsedClosed;

                var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, value) in values)
                {
                    if (key.StartsWith(MetadataPropertyPrefix, StringComparison.Ordinal))
                    {
                        metadata[DecodeMetadataKey(key[MetadataPropertyPrefix.Length..])] = value;
                    }
                }

                var shardId = JobShardId.FromJournalId(storageId);
                return new(provider, storageId, shardId, properties, owner, membershipVersion, minDueTime, maxDueTime, adoptedCount, poisoned, closed, metadata);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                return null;
            }
        }
    }
}
