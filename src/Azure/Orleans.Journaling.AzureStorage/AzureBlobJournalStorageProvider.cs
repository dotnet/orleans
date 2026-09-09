using System.Runtime.CompilerServices;
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
        ILogger<AzureBlobJournalStorage> logger,
        AzureBlobJournalStorageInstruments? instruments = null)
    {
        _options = options.Value;
        _containerFactory = _options.BuildContainerFactory(serviceProvider, _options);
        var journalFormatKey = ValidateJournalFormatKey(managerOptions.Value.JournalFormatKey);
        var journalFormat = GetJournalFormat(serviceProvider, journalFormatKey);
        _shared = new AzureBlobJournalStorage.AzureBlobJournalStorageShared(
            logger,
            options,
            new AzureBlobJournalStorage.OptionsBlobClientProvider(_containerFactory, _options),
            instruments ?? AzureBlobJournalStorageInstruments.CreateForDirectConstruction(),
            mimeType: journalFormat.MimeType,
            journalFormatKey: journalFormatKey);
    }

    private async Task Initialize(CancellationToken cancellationToken)
    {
        var client = await _options.CreateClient!(cancellationToken);
        var container = client.GetBlobContainerClient(_options.ContainerName);
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await _containerFactory.InitializeAsync(client, cancellationToken).ConfigureAwait(false);
        _defaultContainer = container;
    }

    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        return new AzureBlobJournalStorage(_shared, journalId);
    }

    public async IAsyncEnumerable<JournalId> ListAsync(
        ListOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prefix = options?.Prefix ?? default;
        var maxId = options?.MaxId ?? default;
        var container = GetDefaultContainerClient();
        var listExactPrefixSeparately = !prefix.IsDefault && !maxId.IsDefault;
        var blobPrefix = prefix.IsDefault ? null : prefix.Value;
        if (listExactPrefixSeparately)
        {
            if (string.CompareOrdinal(prefix.Value, maxId.Value) > 0)
            {
                yield break;
            }

            // The exact prefix's WAL can sort after every timestamp-named descendant.
            // Discover it independently so it does not prevent the descendant range cutoff.
            if (await HasExactPrefixWalAsync(container, prefix, cancellationToken).ConfigureAwait(false))
            {
                yield return prefix;
            }

            cancellationToken.ThrowIfCancellationRequested();
            blobPrefix = prefix.Value + "/";
            if (string.CompareOrdinal(blobPrefix, maxId.Value) > 0)
            {
                yield break;
            }
        }

        var maxBlobName = GetMaxBlobName(prefix, maxId);

        await foreach (var page in container.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.None,
            prefix: blobPrefix,
            cancellationToken: cancellationToken).AsPages(pageSizeHint: 5000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in page.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Azure's flat List Blobs API returns names in lexical order. Every matching WAL
                // is at or below this raw-name bound, including the WAL for MaxId itself.
                if (maxBlobName is not null && string.CompareOrdinal(item.Name, maxBlobName) > 0)
                {
                    yield break;
                }

                if (item.Properties.BlobType is { } blobType && blobType != BlobType.Append
                    || !item.Name.EndsWith("/wal", StringComparison.Ordinal))
                {
                    continue;
                }

                if (TryParseJournalId(item.Name[..^"/wal".Length], out var journalId)
                    && prefix.IsPrefixOf(journalId)
                    && (!listExactPrefixSeparately || journalId != prefix)
                    && (maxId.IsDefault || string.CompareOrdinal(journalId.Value, maxId.Value) <= 0))
                {
                    yield return journalId;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async ValueTask<bool> HasExactPrefixWalAsync(
        BlobContainerClient container,
        JournalId prefix,
        CancellationToken cancellationToken)
    {
        var name = prefix.Value + "/wal";
        await foreach (var page in container.GetBlobsAsync(
            traits: BlobTraits.None,
            states: BlobStates.None,
            prefix: name,
            cancellationToken: cancellationToken).AsPages(pageSizeHint: 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in page.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The exact name sorts before every longer name with this native prefix.
                return string.Equals(item.Name, name, StringComparison.Ordinal)
                    && item.Properties.BlobType is null or BlobType.Append;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(AzureBlobJournalStorageProvider),
            ServiceLifecycleStage.RuntimeInitialize,
            onStart: Initialize);
    }

    private BlobContainerClient GetDefaultContainerClient()
        => _defaultContainer ?? throw new InvalidOperationException(
            $"{nameof(AzureBlobJournalStorageProvider)} has not been initialized. Ensure the silo lifecycle has started before using journal storage.");

    private static bool TryParseJournalId(string value, out JournalId journalId)
    {
        try
        {
            journalId = new JournalId(value);
            return true;
        }
        catch (ArgumentException)
        {
            journalId = default;
            return false;
        }
    }

    private static string? GetMaxBlobName(JournalId prefix, JournalId maxId)
    {
        if (maxId.IsDefault)
        {
            return null;
        }

        foreach (var character in maxId.Value)
        {
            if (character > '\u007f')
            {
                // Only ASCII bounds avoid relying on UTF-8 versus UTF-16 ordering differences.
                return null;
            }
        }

        var result = maxId.Value + "/wal";
        // Appending "/wal" does not preserve ordering when one id is a prefix of another.
        // Include every possible shorter matching id's WAL in the raw-name upper bound.
        // A non-default exact prefix was already handled separately; only descendants remain.
        for (var length = prefix.IsDefault ? 1 : prefix.Value.Length + 1; length < maxId.Value.Length; length++)
        {
            if (maxId.Value[length] <= '/')
            {
                var candidate = maxId.Value[..length] + "/wal";
                if (string.CompareOrdinal(candidate, result) > 0)
                {
                    result = candidate;
                }
            }
        }

        return result;
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
