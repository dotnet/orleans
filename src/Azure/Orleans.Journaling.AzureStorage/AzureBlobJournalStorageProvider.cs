using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal sealed class AzureBlobJournalStorageProvider(
    IOptions<AzureBlobJournalStorageOptions> options,
    IOptions<JournaledStateManagerOptions> managerOptions,
    IServiceProvider serviceProvider,
    ILogger<AzureBlobJournalStorage> logger) : ILifecycleParticipant<ISiloLifecycle>
{
    private readonly IBlobContainerFactory _containerFactory = options.Value.BuildContainerFactory(serviceProvider, options.Value);
    private readonly string _journalFormatKey = ValidateJournalFormatKey(managerOptions.Value.JournalFormatKey);
    private readonly AzureBlobJournalStorageOptions _options = options.Value;

    private async Task Initialize(CancellationToken cancellationToken)
    {
        var client = await _options.CreateClient!(cancellationToken);
        await _containerFactory.InitializeAsync(client, cancellationToken).ConfigureAwait(false);
    }

    public IJournalStorage Create(IGrainContext grainContext)
    {
        var journalFormatKey = _journalFormatKey;
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

        var container = _containerFactory.GetBlobContainerClient(grainContext.GrainId);
        var blobName = _options.GetBlobNameForJournal(grainContext.GrainId);
        var blobClient = container.GetAppendBlobClient(blobName);
        return new AzureBlobJournalStorage(
            blobClient,
            journalFormat.MimeType,
            logger,
            journalFormatKey: journalFormatKey);
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(AzureBlobJournalStorageProvider),
            ServiceLifecycleStage.RuntimeInitialize,
            onStart: Initialize);
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
