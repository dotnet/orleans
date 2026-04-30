using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal sealed class AzureAppendBlobLogStorageProvider(
    IOptions<AzureAppendBlobLogStorageOptions> options,
    IServiceProvider serviceProvider,
    ILogger<AzureAppendBlobLogStorage> logger) : ILogStorageProvider, ILogFormatKeyProvider, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly IBlobContainerFactory _containerFactory = options.Value.BuildContainerFactory(serviceProvider, options.Value);
    private readonly AzureAppendBlobLogStorageOptions _options = options.Value;

    private async Task Initialize(CancellationToken cancellationToken)
    {
        var client = await _options.CreateClient!(cancellationToken);
        await _containerFactory.InitializeAsync(client, cancellationToken).ConfigureAwait(false);
    }

    public ILogStorage Create(IGrainContext grainContext)
    {
        var container = _containerFactory.GetBlobContainerClient(grainContext.GrainId);
        var blobName = _options.GetBlobName(grainContext.GrainId);
        var blobClient = container.GetAppendBlobClient(blobName);
        return new AzureAppendBlobLogStorage(blobClient, logger);
    }

    public string GetLogFormatKey(IGrainContext grainContext)
        => _options.GetLogFormatKey(grainContext.GrainId.Type);

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(AzureAppendBlobLogStorageProvider),
            ServiceLifecycleStage.RuntimeInitialize,
            onStart: Initialize);
    }
}
