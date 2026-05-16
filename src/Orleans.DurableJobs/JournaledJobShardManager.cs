using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

internal sealed class JournaledJobShardManager : JobShardManager
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

    private readonly IJournaledStateManagerFactory _stateManagerFactory;
    private readonly IJournalStorageCatalog _catalog;
    private readonly IClusterMembershipService _membershipService;
    private readonly IServiceProvider _serviceProvider;
    private readonly DurableJobsOptions _options;
    private readonly JournaledStateManagerOptions _journaledStateManagerOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, JournaledJobShard> _jobShardCache = new();

    public JournaledJobShardManager(
        ILocalSiloDetails localSiloDetails,
        IJournaledStateManagerFactory stateManagerFactory,
        IJournalStorageCatalog catalog,
        IClusterMembershipService membershipService,
        IServiceProvider serviceProvider,
        IOptions<DurableJobsOptions> options,
        IOptions<JournaledStateManagerOptions> journaledStateManagerOptions)
        : base(GetSiloAddress(localSiloDetails))
    {
        ArgumentNullException.ThrowIfNull(localSiloDetails);
        ArgumentNullException.ThrowIfNull(stateManagerFactory);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(membershipService);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(journaledStateManagerOptions);

        _stateManagerFactory = stateManagerFactory;
        _catalog = catalog;
        _membershipService = membershipService;
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _journaledStateManagerOptions = journaledStateManagerOptions.Value;
        _timeProvider = serviceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
    }

    private static SiloAddress GetSiloAddress(ILocalSiloDetails localSiloDetails)
    {
        ArgumentNullException.ThrowIfNull(localSiloDetails);
        return localSiloDetails.SiloAddress;
    }

    public override async Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxDueTime, int maxNewClaims, CancellationToken cancellationToken)
    {
        var result = new List<IJobShard>();
        var newClaimCount = 0;
        var membershipSnapshot = _membershipService.CurrentSnapshot;

        await foreach (var storageId in _catalog.ListAsync(JobShardId.StoragePrefix, cancellationToken))
        {
            var descriptor = await GetDescriptorAsync(storageId, cancellationToken);
            if (descriptor is null || descriptor.Poisoned || descriptor.StartTime > maxDueTime)
            {
                continue;
            }

            if (descriptor.MembershipVersion > membershipSnapshot.Version)
            {
                await _membershipService.Refresh(descriptor.MembershipVersion, cancellationToken);
                membershipSnapshot = _membershipService.CurrentSnapshot;
            }

            if (descriptor.Owner is { } owner && owner.Equals(SiloAddress))
            {
                result.Add(await GetOrOpenShardAsync(descriptor, cancellationToken));
                continue;
            }

            var isAdopted = false;
            if (descriptor.Owner is { } previousOwner)
            {
                var ownerStatus = membershipSnapshot.GetSiloStatus(previousOwner);
                if (ownerStatus is not SiloStatus.Dead and not SiloStatus.None)
                {
                    continue;
                }

                isAdopted = ownerStatus == SiloStatus.Dead;
            }

            if (newClaimCount >= maxNewClaims)
            {
                continue;
            }

            var claimedShard = await TryClaimShardAsync(descriptor, isAdopted, cancellationToken);
            if (claimedShard is null)
            {
                continue;
            }

            _jobShardCache[claimedShard.Id] = claimedShard;
            result.Add(claimedShard);
            newClaimCount++;
        }

        return result;
    }

    public override async Task<IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        while (true)
        {
            var shardId = JobShardId.New();
            var storageId = shardId.ToJournalStorageId();
            var initialProperties = CreateInitialProperties(minDueTime, maxDueTime, metadata);
            var createResult = await _catalog.CreateIfNotExistsAsync(storageId, initialProperties, cancellationToken);
            if (createResult.Status is not JournalStorageCreateStatus.Created)
            {
                continue;
            }

            var properties = createResult.Properties ?? await _catalog.GetPropertiesAsync(storageId, cancellationToken);
            var descriptor = properties is not null ? ShardCatalogProperties.From(storageId, properties) : null;
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
            var descriptor = await GetDescriptorAsync(journaledShard.StorageId, cancellationToken)
                ?? throw new InvalidOperationException($"Cannot unregister DurableJobs shard '{shard.Id}' because its catalog properties were not found.");

            if (descriptor.Owner is null || !descriptor.Owner.Equals(SiloAddress))
            {
                throw new InvalidOperationException("Cannot unregister a DurableJobs shard owned by another silo.");
            }

            var count = await shard.GetJobCountAsync();
            if (count == 0)
            {
                await journaledShard.DeleteStateAsync(cancellationToken);
            }
            else
            {
                var update = new JournalStoragePropertiesUpdate(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [ClosedProperty] = bool.TrueString,
                        [MembershipVersionProperty] = GetMembershipVersionString()
                    },
                    [OwnerProperty, AdoptedCountProperty, LastAdoptedTimeProperty]);

                var updateResult = await _catalog.UpdatePropertiesAsync(
                    descriptor.StorageId,
                    update,
                    descriptor.Properties.ETag,
                    cancellationToken);

                if (updateResult.Status is not JournalStoragePropertiesUpdateStatus.Updated and not JournalStoragePropertiesUpdateStatus.NoChange)
                {
                    throw new InvalidOperationException($"Failed to release DurableJobs shard '{shard.Id}' ownership. Catalog update status: {updateResult.Status}.");
                }
            }
        }
        finally
        {
            _jobShardCache.TryRemove(shard.Id, out _);
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
        var descriptor = await GetDescriptorAsync(shardId, cancellationToken);
        return descriptor is { Poisoned: false, Owner: { } owner } && owner.Equals(SiloAddress);
    }

    internal async ValueTask<bool> TryMarkShardClosedAsync(string shardId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var descriptor = await GetDescriptorAsync(shardId, cancellationToken);
            if (descriptor is null || descriptor.Poisoned || descriptor.Owner is null || !descriptor.Owner.Equals(SiloAddress))
            {
                return false;
            }

            if (descriptor.Closed)
            {
                return true;
            }

            var update = new JournalStoragePropertiesUpdate(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ClosedProperty] = bool.TrueString,
                    [MembershipVersionProperty] = GetMembershipVersionString()
                },
                remove: null);

            var result = await _catalog.UpdatePropertiesAsync(descriptor.StorageId, update, descriptor.Properties.ETag, cancellationToken);
            if (result.Status is JournalStoragePropertiesUpdateStatus.Updated or JournalStoragePropertiesUpdateStatus.NoChange)
            {
                return true;
            }

            if (result.Status is not JournalStoragePropertiesUpdateStatus.ETagMismatch)
            {
                return false;
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
            [ClosedProperty] = bool.TrueString
        };
        List<string>? remove = null;

        if (isAdopted)
        {
            adoptedCount++;
            if (adoptedCount > _options.MaxAdoptedCount)
            {
                await TryMarkShardPoisonedAsync(descriptor, adoptedCount, cancellationToken);
                return null;
            }

            set[AdoptedCountProperty] = adoptedCount.ToString(CultureInfo.InvariantCulture);
            set[LastAdoptedTimeProperty] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        }
        else
        {
            set[AdoptedCountProperty] = "0";
            remove = [LastAdoptedTimeProperty];
        }

        var updateResult = await _catalog.UpdatePropertiesAsync(
            descriptor.StorageId,
            new JournalStoragePropertiesUpdate(set, remove),
            descriptor.Properties.ETag,
            cancellationToken);

        if (updateResult.Status is not JournalStoragePropertiesUpdateStatus.Updated and not JournalStoragePropertiesUpdateStatus.NoChange
            || updateResult.Properties is null)
        {
            return null;
        }

        var updatedDescriptor = ShardCatalogProperties.From(descriptor.StorageId, updateResult.Properties);
        return updatedDescriptor is null || updatedDescriptor.Owner is null || !updatedDescriptor.Owner.Equals(SiloAddress)
            ? null
            : await OpenShardAsync(updatedDescriptor, cancellationToken);
    }

    private async Task TryMarkShardPoisonedAsync(ShardCatalogProperties descriptor, int adoptedCount, CancellationToken cancellationToken)
    {
        var update = new JournalStoragePropertiesUpdate(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PoisonedProperty] = bool.TrueString,
                [AdoptedCountProperty] = adoptedCount.ToString(CultureInfo.InvariantCulture),
                [LastAdoptedTimeProperty] = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                [MembershipVersionProperty] = GetMembershipVersionString()
            },
            remove: null);

        await _catalog.UpdatePropertiesAsync(descriptor.StorageId, update, descriptor.Properties.ETag, cancellationToken);
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
        var manager = _stateManagerFactory.Create(new JournalId(descriptor.StorageId.Value));
        try
        {
            manager.RegisterState(JournaledJobShardState.StateName, state);
            await manager.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await manager.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new JournaledJobShard(
            descriptor.ShardId,
            descriptor.StartTime,
            descriptor.EndTime,
            descriptor.Metadata,
            descriptor.Closed,
            state,
            manager,
            this);
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
        try
        {
            return await GetDescriptorAsync(JobShardId.Parse(shardId).ToJournalStorageId(), cancellationToken);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async ValueTask<ShardCatalogProperties?> GetDescriptorAsync(JournalStorageId storageId, CancellationToken cancellationToken)
    {
        var properties = await _catalog.GetPropertiesAsync(storageId, cancellationToken);
        return properties is null ? null : ShardCatalogProperties.From(storageId, properties);
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

    private sealed class ShardCatalogProperties
    {
        private ShardCatalogProperties(
            JournalStorageId storageId,
            JobShardId shardId,
            JournalStorageProperties properties,
            SiloAddress? owner,
            MembershipVersion membershipVersion,
            DateTimeOffset startTime,
            DateTimeOffset endTime,
            int adoptedCount,
            bool poisoned,
            bool closed,
            IReadOnlyDictionary<string, string> metadata)
        {
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

        public JournalStorageId StorageId { get; }

        public JobShardId ShardId { get; }

        public JournalStorageProperties Properties { get; }

        public SiloAddress? Owner { get; }

        public MembershipVersion MembershipVersion { get; }

        public DateTimeOffset StartTime { get; }

        public DateTimeOffset EndTime { get; }

        public int AdoptedCount { get; }

        public bool Poisoned { get; }

        public bool Closed { get; }

        public IReadOnlyDictionary<string, string> Metadata { get; }

        public static ShardCatalogProperties? From(JournalStorageId storageId, JournalStorageProperties properties)
        {
            try
            {
                var values = properties.Values;
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

                var shardId = JobShardId.FromJournalStorageId(storageId);
                return new(storageId, shardId, properties, owner, membershipVersion, minDueTime, maxDueTime, adoptedCount, poisoned, closed, metadata);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                return null;
            }
        }
    }
}
