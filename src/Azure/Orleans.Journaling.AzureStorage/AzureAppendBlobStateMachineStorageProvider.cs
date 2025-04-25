using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal sealed class AzureAppendBlobStateMachineStorageProvider(
    IOptions<AzureAppendBlobStateMachineStorageOptions> options,
    IServiceProvider serviceProvider,
    ILogger<AzureAppendBlobLogStorage> logger) : IStateMachineStorageProvider, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly IBlobContainerFactory _containerFactory = options.Value.BuildContainerFactory(serviceProvider, options.Value);
    private readonly AzureAppendBlobStateMachineStorageOptions _options = options.Value;

    private async Task Initialize(CancellationToken cancellationToken)
    {
        var client = await _options.CreateClient!(cancellationToken);
        await _containerFactory.InitializeAsync(client, cancellationToken).ConfigureAwait(false);
    }

    public IStateMachineStorage Create(IGrainContext grainContext)
    {
        var container = _containerFactory.GetBlobContainerClient(grainContext.GrainId);
        var blobName = _options.GetBlobName(grainContext.GrainId);
        var blobClient = container.GetAppendBlobClient(blobName);
        return new AzureAppendBlobLogStorage(blobClient, logger);
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(AzureAppendBlobStateMachineStorageProvider),
            ServiceLifecycleStage.RuntimeInitialize,
            onStart: Initialize);
    }
}
