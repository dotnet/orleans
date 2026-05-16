using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.Journaling;

internal sealed class AzureBlobJournalStorageProvider : ILifecycleParticipant<ISiloLifecycle>, IJournalStorageProvider
{
    private readonly IBlobContainerFactory _containerFactory;
    private readonly AzureBlobJournalStorageOptions _options;
    private readonly AzureBlobJournalStorage.AzureBlobJournalStorageShared _shared;

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
        await _containerFactory.InitializeAsync(client, cancellationToken).ConfigureAwait(false);
    }

    public IJournalStorage CreateStorage(IGrainContext grainContext)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        return CreateStorage(JournalId.FromGrainId(grainContext.GrainId));
    }

    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        return new AzureBlobJournalStorage(_shared, journalId);
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(AzureBlobJournalStorageProvider),
            ServiceLifecycleStage.RuntimeInitialize,
            onStart: Initialize);
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
