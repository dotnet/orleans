using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal sealed class AzureBlobJournalStorageProvider : ILifecycleParticipant<ISiloLifecycle>, IJournalStorageProvider, IJournalStorageCatalog
{
    private readonly IBlobContainerFactory _containerFactory;
    private readonly AzureBlobJournalStorageOptions _options;
    private readonly AzureBlobJournalStorage.AzureBlobJournalStorageShared _shared;
    private BlobContainerClient? _defaultContainer;

    public AzureBlobJournalStorageProvider(
        IOptions<AzureBlobJournalStorageOptions> options,
        IOptions<JournaledStateManagerOptions> managerOptions,
        IServiceProvider serviceProvider,
        ILogger<AzureBlobJournalStorage> logger)
    {
        _options = options.Value;
        _containerFactory = _options.BuildContainerFactory(serviceProvider, _options);
        var journalFormatKey = ValidateJournalFormatKey(managerOptions.Value.JournalFormatKey);
        var journalFormat = GetJournalFormat(serviceProvider, journalFormatKey);
        _shared = new AzureBlobJournalStorage.AzureBlobJournalStorageShared(
            logger,
            options,
            new AzureBlobJournalStorage.OptionsBlobClientProvider(_containerFactory, _options),
            mimeType: journalFormat.MimeType,
            journalFormatKey: journalFormatKey);
    }

    private async Task Initialize(CancellationToken cancellationToken)
    {
        var client = await _options.CreateClient!(cancellationToken);
        _defaultContainer = client.GetBlobContainerClient(_options.ContainerName);
        await _defaultContainer.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await _containerFactory.InitializeAsync(client, cancellationToken).ConfigureAwait(false);
    }

    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        return new AzureBlobJournalStorage(_shared, journalId);
    }

    public async IAsyncEnumerable<JournalStorageId> ListAsync(
        JournalStoragePrefix prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        var container = GetDefaultContainerClient();
        var blobPrefix = prefix.IsRoot ? null : prefix.Value;
        var storageIds = new List<JournalStorageId>();
        await foreach (var item in container.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.None,
            prefix: blobPrefix,
            cancellationToken: cancellationToken))
        {
            if (item.Properties.BlobType is { } blobType && blobType != BlobType.Append)
            {
                continue;
            }

            if (!item.Name.EndsWith("/wal", StringComparison.Ordinal))
            {
                continue;
            }

            var storageIdValue = item.Name[..^"/wal".Length];
            if (TryParseStorageId(storageIdValue, out var storageId) && prefix.Matches(storageId))
            {
                storageIds.Add(storageId);
            }
        }

        foreach (var storageId in storageIds.OrderBy(static storageId => storageId.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return storageId;
        }
    }

    public async ValueTask<JournalStorageCreateResult> CreateIfNotExistsAsync(
        JournalStorageId storageId,
        IReadOnlyDictionary<string, string>? properties = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storageId);
        ValidateCallerProperties(properties);

        var blobClient = GetWalClient(storageId);
        var metadata = CreateWalMetadata(properties);
        try
        {
            var response = await blobClient.CreateAsync(
                new AppendBlobCreateOptions
                {
                    Conditions = new AppendBlobRequestConditions { IfNoneMatch = ETag.All },
                    HttpHeaders = CreateHttpHeaders(_shared.MimeType),
                    Metadata = metadata,
                },
                cancellationToken).ConfigureAwait(false);

            return new(
                JournalStorageCreateStatus.Created,
                CreateJournalStorageProperties(response.Value.ETag, metadata));
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            var existing = await GetPropertiesCoreAsync(blobClient, conditions: null, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return new(JournalStorageCreateStatus.Conflict, properties: null);
            }

            var current = CreateJournalStorageProperties(existing.ETag, existing.Metadata);
            var status = existing.BlobType == BlobType.Append && InitialPropertiesMatch(current.Values, properties)
                ? JournalStorageCreateStatus.AlreadyExists
                : JournalStorageCreateStatus.Conflict;
            return new(status, current);
        }
    }

    public async ValueTask<JournalStorageProperties?> GetPropertiesAsync(JournalStorageId storageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storageId);

        var properties = await GetPropertiesCoreAsync(GetWalClient(storageId), conditions: null, cancellationToken).ConfigureAwait(false);
        if (properties is null || properties.BlobType != BlobType.Append)
        {
            return null;
        }

        return CreateJournalStorageProperties(properties.ETag, properties.Metadata);
    }

    public async ValueTask<JournalStoragePropertiesUpdateResult> UpdatePropertiesAsync(
        JournalStorageId storageId,
        JournalStoragePropertiesUpdate update,
        string? expectedETag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storageId);
        ArgumentNullException.ThrowIfNull(update);

        var blobClient = GetWalClient(storageId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            BlobProperties? properties;
            try
            {
                properties = await GetPropertiesCoreAsync(
                    blobClient,
                    expectedETag is null ? null : new BlobRequestConditions { IfMatch = ToAzureETag(expectedETag) },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (exception.Status is 412)
            {
                return new(
                    JournalStoragePropertiesUpdateStatus.ETagMismatch,
                    await GetPropertiesAsync(storageId, cancellationToken).ConfigureAwait(false));
            }

            if (properties is null || properties.BlobType != BlobType.Append)
            {
                return new(JournalStoragePropertiesUpdateStatus.NotFound, properties: null);
            }

            var current = CreateJournalStorageProperties(properties.ETag, properties.Metadata);
            var metadata = CopyMetadata(properties.Metadata);
            if (!ApplyCallerPropertyUpdate(metadata, update))
            {
                return new(JournalStoragePropertiesUpdateStatus.NoChange, current);
            }

            var conditions = new BlobRequestConditions
            {
                IfMatch = expectedETag is null ? properties.ETag : ToAzureETag(expectedETag),
            };

            try
            {
                var response = await blobClient.SetMetadataAsync(metadata, conditions, cancellationToken).ConfigureAwait(false);
                return new(
                    JournalStoragePropertiesUpdateStatus.Updated,
                    CreateJournalStorageProperties(response.Value.ETag, metadata));
            }
            catch (RequestFailedException exception) when (exception.Status is 412)
            {
                if (expectedETag is not null)
                {
                    return new(
                        JournalStoragePropertiesUpdateStatus.ETagMismatch,
                        await GetPropertiesAsync(storageId, cancellationToken).ConfigureAwait(false));
                }
            }
        }

        return new(
            JournalStoragePropertiesUpdateStatus.ETagMismatch,
            await GetPropertiesAsync(storageId, cancellationToken).ConfigureAwait(false));
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(AzureBlobJournalStorageProvider),
            ServiceLifecycleStage.RuntimeInitialize,
            onStart: Initialize);
    }

    private AppendBlobClient GetWalClient(JournalStorageId storageId)
    {
        var journalId = ToJournalId(storageId);
        var journalBlobName = _options.GetBlobNameForJournal(storageId);
        var walBlobName = AzureBlobJournalStorageOptions.GetWalBlobNameForJournal(journalId, journalBlobName);
        return GetDefaultContainerClient().GetAppendBlobClient(walBlobName);
    }

    private BlobContainerClient GetDefaultContainerClient()
        => _defaultContainer ?? throw new InvalidOperationException(
            $"{nameof(AzureBlobJournalStorageProvider)} has not been initialized. Ensure the silo lifecycle has started before using journal storage.");

    private static JournalId ToJournalId(JournalStorageId storageId) => new(storageId.Value);

    private Dictionary<string, string> CreateWalMetadata(IReadOnlyDictionary<string, string>? properties)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_shared.JournalFormatKey is { Length: > 0 })
        {
            metadata[AzureBlobJournalStorage.FormatMetadataKey] = _shared.JournalFormatKey;
        }

        if (properties is not null)
        {
            foreach (var (key, value) in properties)
            {
                ValidateCallerProperty(key, value);
                metadata[key] = value;
            }
        }

        return metadata;
    }

    private static BlobHttpHeaders? CreateHttpHeaders(string? contentType)
        => contentType is { Length: > 0 } ? new BlobHttpHeaders { ContentType = contentType } : null;

    private static async ValueTask<BlobProperties?> GetPropertiesCoreAsync(
        AppendBlobClient blobClient,
        BlobRequestConditions? conditions,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await blobClient.GetPropertiesAsync(conditions, cancellationToken).ConfigureAwait(false);
            return response.Value;
        }
        catch (RequestFailedException exception) when (exception.Status is 404)
        {
            return null;
        }
    }

    private static JournalStorageProperties CreateJournalStorageProperties(ETag eTag, IDictionary<string, string>? metadata)
        => new(eTag.ToString(), CopyCallerProperties(metadata));

    private static Dictionary<string, string> CopyCallerProperties(IDictionary<string, string>? metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata is null)
        {
            return result;
        }

        foreach (var (key, value) in metadata)
        {
            if (IsProviderMetadataKey(key))
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    private static Dictionary<string, string> CopyMetadata(IDictionary<string, string>? metadata)
        => metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);

    private static bool InitialPropertiesMatch(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return true;
        }

        foreach (var (key, value) in requested)
        {
            if (!current.TryGetValue(key, out var currentValue)
                || !string.Equals(currentValue, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateCallerProperties(IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null)
        {
            return;
        }

        foreach (var (key, value) in properties)
        {
            ValidateCallerProperty(key, value);
        }
    }

    private static void ValidateCallerProperty(string key, string value)
    {
        ValidateCallerPropertyName(key);
        if (IsProviderMetadataKey(key))
        {
            throw new ArgumentException($"Journal storage property '{key}' is provider-owned.", nameof(key));
        }

        ArgumentNullException.ThrowIfNull(value);
    }

    private static bool ApplyCallerPropertyUpdate(Dictionary<string, string> metadata, JournalStoragePropertiesUpdate update)
    {
        var changed = false;
        foreach (var propertyName in update.Remove)
        {
            if (IsProviderMetadataKey(propertyName))
            {
                throw new ArgumentException($"Journal storage property '{propertyName}' is provider-owned.", nameof(update));
            }

            changed |= metadata.Remove(propertyName);
        }

        foreach (var (propertyName, value) in update.Set)
        {
            ValidateCallerProperty(propertyName, value);
            if (!metadata.TryGetValue(propertyName, out var currentValue)
                || !string.Equals(currentValue, value, StringComparison.Ordinal))
            {
                metadata[propertyName] = value;
                changed = true;
            }
        }

        return changed;
    }

    private static bool IsProviderMetadataKey(string key)
        => string.Equals(key, AzureBlobJournalStorage.FormatMetadataKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, AzureBlobJournalStorage.CheckpointMetadataKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, AzureBlobJournalStorage.CheckpointOffsetMetadataKey, StringComparison.OrdinalIgnoreCase);

    private static void ValidateCallerPropertyName(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        if (propertyName.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Journal storage property names must not contain null characters.", nameof(propertyName));
        }

        if (JournalStoragePropertyNames.IsProviderOwned(propertyName))
        {
            throw new ArgumentException(
                $"Journal storage property '{propertyName}' is provider-owned. Caller updates must not set or remove provider-owned properties.",
                nameof(propertyName));
        }
    }

    private static bool TryParseStorageId(string value, [NotNullWhen(true)] out JournalStorageId? storageId)
    {
        try
        {
            storageId = JournalStorageId.Parse(value);
            return true;
        }
        catch (ArgumentException)
        {
            storageId = null;
            return false;
        }
    }

    private static ETag ToAzureETag(string eTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eTag);
        return new ETag(eTag);
    }

    private static IJournalFormat GetJournalFormat(IServiceProvider serviceProvider, string journalFormatKey)
    {
        var journalFormat = serviceProvider.GetKeyedService<IJournalFormat>(journalFormatKey);
        if (journalFormat is null)
        {
            throw new InvalidOperationException(
                $"Journal format key '{journalFormatKey}' requires keyed service '{typeof(IJournalFormat).FullName}', but none was registered.");
        }

        if (!string.Equals(journalFormat.FormatKey, journalFormatKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Journal format key '{journalFormatKey}' resolved format '{journalFormat.GetType().FullName}', but its {nameof(IJournalFormat.FormatKey)} is '{journalFormat.FormatKey}'. " +
                "Register the journal format using the same key it reports.");
        }

        return journalFormat;
    }

    private static string ValidateJournalFormatKey(string? journalFormatKey)
    {
        if (string.IsNullOrWhiteSpace(journalFormatKey))
        {
            throw new InvalidOperationException("The configured journal format key must be non-empty.");
        }

        return journalFormatKey;
    }
}
