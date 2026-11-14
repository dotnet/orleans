using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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

        var container = GetDefaultContainerClient();
        var prefix = range.ListingPrefix ?? string.Empty;
        var maxBlobName = GetNativeBound(range.MaxId, prefix, isUpperBound: true);
        var startFrom = GetNativeBound(range.LowerBound, prefix, isUpperBound: false);
        await foreach (var page in container.GetBlobsAsync(
            new GetBlobsOptions
            {
                Traits = range.IncludeMetadata ? BlobTraits.Metadata : BlobTraits.None,
                Prefix = AzureBlobJournalStorageLayout.GetWalBlobName(prefix),
                StartFrom = startFrom,
            },
            cancellationToken).AsPages(pageSizeHint: 5000))
        {
            _shared.Instruments.OnCatalogPage(page.Values.Count);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in page.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The native bound encloses every ordinal match in both flat and HNS traversal.
                if (maxBlobName is not null && string.CompareOrdinal(item.Name, maxBlobName) > 0)
                {
                    yield break;
                }

                if (item.Properties.BlobType is { } blobType && blobType != BlobType.Append)
                {
                    continue;
                }

                if (AzureBlobJournalStorageLayout.TryGetJournalId(item.Name, out var journalId)
                    && range.Contains(journalId.Value))
                {
                    var entry = new JournalCatalogEntry(
                        journalId,
                        range.IncludeMetadata
                            ? AzureBlobJournalStorage.CreateJournalMetadata(item.Properties.ETag!.Value, item.Metadata)
                            : null);
                    _shared.Instruments.OnCatalogEntry();
                    yield return entry;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string? GetNativeBound(string? bound, string prefix, bool isUpperBound)
    {
        if (bound is null || !System.Text.Ascii.IsValid(bound))
        {
            return null;
        }

        var index = 0;
        while (index < bound.Length && index < prefix.Length && bound[index] == prefix[index])
        {
            index++;
        }

        // HNS sorts '/' first. Beyond the shared prefix, keep boundary characters above '/'
        // so both service orderings agree, widening the requested interval where necessary.
        for (; index < bound.Length; index++)
        {
            if (bound[index] <= '/')
            {
                var widened = isUpperBound ? string.Concat(bound.AsSpan(0, index), "0") : bound[..index];
                return AzureBlobJournalStorageLayout.GetWalBlobName(widened);
            }
        }

        return AzureBlobJournalStorageLayout.GetWalBlobName(bound);
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
