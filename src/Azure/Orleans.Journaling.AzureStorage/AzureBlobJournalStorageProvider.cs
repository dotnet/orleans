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

    public async IAsyncEnumerable<JournalId> ListAsync(
        JournalId prefix = default,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var container = GetDefaultContainerClient();
        var blobPrefix = prefix.IsDefault ? null : prefix.Value;
        var journalIds = new List<JournalId>();
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
            if (TryParseJournalId(storageIdValue, out var journalId) && prefix.IsPrefixOf(journalId))
            {
                journalIds.Add(journalId);
            }
        }

        foreach (var journalId in journalIds.OrderBy(static journalId => journalId.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return journalId;
        }
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
