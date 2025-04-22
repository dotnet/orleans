using Azure;
using Azure.Storage.Blobs;
using Azure.Storage;
using Azure.Core;
using Orleans.Runtime;

namespace Orleans.Journaling;

/// <summary>
/// Options for configuring the Azure Append Blob state machine storage provider.
/// </summary>
public sealed class AzureAppendBlobStateMachineStorageOptions
{
    private BlobServiceClient? _blobServiceClient;

    /// <summary>
    /// Container name where state machine state is stored.
    /// </summary>
    public string ContainerName { get; set; } = DEFAULT_CONTAINER_NAME;
    public const string DEFAULT_CONTAINER_NAME = "state";

    /// <summary>
    /// Gets or sets the delegate used to generate the blob name for a given grain.
    /// </summary>
    public Func<GrainId, string> GetBlobName { get; set; } = DefaultGetBlobName;

    private static readonly Func<GrainId, string> DefaultGetBlobName = static (GrainId grainId) => $"{grainId}.bin";

    /// <summary>
    /// Options to be used when configuring the blob storage client, or <see langword="null"/> to use the default options.
    /// </summary>
    public BlobClientOptions? ClientOptions { get; set; }

    /// <summary>
    /// Gets or sets the client used to access the Azure Blob Service.
    /// </summary>
    public BlobServiceClient? BlobServiceClient
    {
        get => _blobServiceClient;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _blobServiceClient = value;
            CreateClient = ct => Task.FromResult(value);
        }
    }

    /// <summary>
    /// The optional delegate used to create a <see cref="BlobServiceClient"/> instance.
    /// </summary>
    internal Func<CancellationToken, Task<BlobServiceClient>>? CreateClient { get; private set; }

    /// <summary>
    /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
    /// </summary>
    public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
    public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

    /// <summary>
    /// A function for building container factory instances.
    /// </summary>
    public Func<IServiceProvider, AzureAppendBlobStateMachineStorageOptions, IBlobContainerFactory> BuildContainerFactory { get; set; }
        = static (provider, options) => new DefaultBlobContainerFactory(options);

    /// <summary>
    /// Configures the <see cref="BlobServiceClient"/> using a connection string.
    /// </summary>
    public void ConfigureBlobServiceClient(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        CreateClient = ct => Task.FromResult(new BlobServiceClient(connectionString, ClientOptions));
    }

    /// <summary>
    /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI.
    /// </summary>
    public void ConfigureBlobServiceClient(Uri serviceUri)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        CreateClient = ct => Task.FromResult(new BlobServiceClient(serviceUri, ClientOptions));
    }

    /// <summary>
    /// Configures the <see cref="BlobServiceClient"/> using the provided callback.
    /// </summary>
    public void ConfigureBlobServiceClient(Func<CancellationToken, Task<BlobServiceClient>> createClientCallback)
    {
        CreateClient = createClientCallback ?? throw new ArgumentNullException(nameof(createClientCallback));
    }

    /// <summary>
    /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI and a <see cref="TokenCredential"/>.
    /// </summary>
    public void ConfigureBlobServiceClient(Uri serviceUri, TokenCredential tokenCredential)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        ArgumentNullException.ThrowIfNull(tokenCredential);
        CreateClient = ct => Task.FromResult(new BlobServiceClient(serviceUri, tokenCredential, ClientOptions));
    }

    /// <summary>
    /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI and a <see cref="AzureSasCredential"/>.
    /// </summary>
    public void ConfigureBlobServiceClient(Uri serviceUri, AzureSasCredential azureSasCredential)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        ArgumentNullException.ThrowIfNull(azureSasCredential);
        CreateClient = ct => Task.FromResult(new BlobServiceClient(serviceUri, azureSasCredential, ClientOptions));
    }

    /// <summary>
    /// Configures the <see cref="BlobServiceClient"/> using an authenticated service URI and a <see cref="StorageSharedKeyCredential"/>.
    /// </summary>
    public void ConfigureBlobServiceClient(Uri serviceUri, StorageSharedKeyCredential sharedKeyCredential)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        ArgumentNullException.ThrowIfNull(sharedKeyCredential);
        CreateClient = ct => Task.FromResult(new BlobServiceClient(serviceUri, sharedKeyCredential, ClientOptions));
    }
}
