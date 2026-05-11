using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal sealed class AzureAppendBlobJournalStorageProvider(
    IOptions<AzureAppendBlobJournalStorageOptions> options,
    IServiceProvider serviceProvider,
    ILogger<AzureAppendBlobJournalStorage> logger) : IJournalStorageProvider, IJournalFormatKeyProvider, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly IBlobContainerFactory _containerFactory = options.Value.BuildContainerFactory(serviceProvider, options.Value);
    private readonly AzureAppendBlobJournalStorageOptions _options = options.Value;

    private async Task Initialize(CancellationToken cancellationToken)
    {
        var client = await _options.CreateClient!(cancellationToken);
        await _containerFactory.InitializeAsync(client, cancellationToken).ConfigureAwait(false);
    }

    public IJournalStorage Create(IGrainContext grainContext)
    {
        var journalFormatKey = _options.GetJournalFormatKey(grainContext.GrainId.Type);
        var journalFormat = serviceProvider.GetKeyedService<IJournalFormat>(journalFormatKey);
        if (journalFormat is null)
        {
            throw new InvalidOperationException(
                $"Journal format key '{journalFormatKey}' requires keyed service '{typeof(IJournalFormat).FullName}', but none was registered.");
        }

        var container = _containerFactory.GetBlobContainerClient(grainContext.GrainId);
        var blobName = _options.GetBlobNameForJournal(grainContext.GrainId);
        var blobClient = container.GetAppendBlobClient(blobName);
        return new AzureAppendBlobJournalStorage(
            blobClient,
            journalFormat.MimeType,
            logger,
            static (client, snapshot) => client.WithSnapshot(snapshot),
            snapshotEnumerator: null,
            journalFormatKey: journalFormatKey);
    }

    public string GetJournalFormatKey(IGrainContext grainContext)
        => _options.GetJournalFormatKey(grainContext.GrainId.Type);

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(AzureAppendBlobJournalStorageProvider),
            ServiceLifecycleStage.RuntimeInitialize,
            onStart: Initialize);
    }
}
