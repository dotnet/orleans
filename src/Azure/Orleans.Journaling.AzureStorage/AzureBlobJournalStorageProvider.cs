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

    private Task Initialize(CancellationToken cancellationToken)
        => _shared.Instruments.Telemetry.TrackOperationAsync(
            JournalStorageTelemetry.AzureBlob, "initialize", () => InitializeCore(cancellationToken));

    private async Task InitializeCore(CancellationToken cancellationToken)
    {
        var client = await _options.CreateClient!(cancellationToken);
        var container = client.GetBlobContainerClient(_options.ContainerName);
        await _shared.Instruments.TrackApiCallAsync(
            nameof(BlobContainerClient.CreateIfNotExistsAsync),
            () => container.CreateIfNotExistsAsync(cancellationToken: cancellationToken),
            static result => result is null ? JournalStorageTelemetry.AlreadyExists
                : JournalStorageTelemetry.GetHttpStatus(result.GetRawResponse().Status)).ConfigureAwait(false);
        if (_containerFactory is DefaultBlobContainerFactory defaultFactory)
        {
            await defaultFactory.InitializeAsync(client, cancellationToken, _shared.Instruments).ConfigureAwait(false);
        }
        else
        {
            await _containerFactory.InitializeAsync(client, cancellationToken).ConfigureAwait(false);
        }

        _defaultContainer = container;
    }

    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        return new InstrumentedJournalStorage(
            new AzureBlobJournalStorage(_shared, journalId), JournalStorageTelemetry.AzureBlob, _shared.Instruments.Telemetry);
    }

    public IAsyncEnumerable<JournalCatalogEntry> ListAsync(
        ListOptions? options = null,
        CancellationToken cancellationToken = default)
        => _shared.Instruments.Telemetry.TrackCatalog(
            JournalStorageTelemetry.AzureBlob, ListCoreAsync(options, cancellationToken));

    private async IAsyncEnumerable<JournalCatalogEntry> ListCoreAsync(
        ListOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var range = new JournalCatalogRange(options);
        if (range.IsEmpty)
        {
            yield break;
        }

        var container = GetDefaultContainerClient();
        var maxBlobName = range.MaxId is { } maxId && System.Text.Ascii.IsValid(maxId)
            ? AzureBlobJournalStorageLayout.GetWalBlobName(maxId) : null;
        var startFrom = range.LowerBound is { } lowerBound && System.Text.Ascii.IsValid(lowerBound)
            ? AzureBlobJournalStorageLayout.GetWalBlobName(lowerBound) : null;
        await foreach (var page in _shared.Instruments.TrackApiPages(nameof(BlobContainerClient.GetBlobsAsync), container.GetBlobsAsync(
            new GetBlobsOptions
            {
                Traits = range.IncludeMetadata ? BlobTraits.Metadata : BlobTraits.None,
                Prefix = AzureBlobJournalStorageLayout.GetWalBlobName(range.ListingPrefix ?? string.Empty),
                StartFrom = startFrom,
            },
            cancellationToken).AsPages(pageSizeHint: 5000)))
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

                if (item.Properties.BlobType is { } blobType && blobType != BlobType.Append)
                {
                    continue;
                }

                if (AzureBlobJournalStorageLayout.TryGetJournalId(item.Name, out var journalId)
                    && range.Contains(journalId.Value))
                {
                    yield return new(
                        journalId,
                        range.IncludeMetadata
                            ? AzureBlobJournalStorage.CreateJournalMetadata(item.Properties.ETag!.Value, item.Metadata)
                            : null);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
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
