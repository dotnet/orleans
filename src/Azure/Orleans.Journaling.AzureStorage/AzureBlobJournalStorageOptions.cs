using Azure;
using Azure.Storage.Blobs;
using Azure.Storage;
using Azure.Core;
using Orleans.Runtime;

namespace Orleans.Journaling;

/// <summary>
/// Options for configuring the Azure Blob state storage provider.
/// </summary>
public sealed class AzureBlobJournalStorageOptions
{
    private BlobServiceClient? _blobServiceClient;

    /// <summary>
    /// Container name where state is stored.
    /// </summary>
    public string ContainerName { get; set; } = DEFAULT_CONTAINER_NAME;
    public const string DEFAULT_CONTAINER_NAME = "state";

    /// <summary>
    /// Gets or sets the delegate used to generate the blob name for a given grain.
    /// </summary>
    public Func<GrainId, string> GetBlobName { get; set; } = DefaultGetBlobName;

    private static readonly Func<GrainId, string> DefaultGetBlobName = static grainId => grainId.ToString();

    /// <summary>
    /// Gets or sets the delegate used to generate block blob names for compacted journal snapshots.
    /// </summary>
    public Func<AzureBlobJournalSnapshotNameContext, string> GetSnapshotBlobName { get; set; } = DefaultGetSnapshotBlobName;

    private static readonly Func<AzureBlobJournalSnapshotNameContext, string> DefaultGetSnapshotBlobName =
        static context => $"{context.JournalBlobName}.snapshots/{context.SnapshotId}";

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

    internal string GetBlobNameForJournal(GrainId grainId)
    {
        var blobName = GetBlobName(grainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        return blobName;
    }

    internal string GetSnapshotBlobNameForJournal(GrainId grainId, string journalBlobName, string snapshotId)
    {
        var blobName = GetSnapshotBlobName(new AzureBlobJournalSnapshotNameContext(grainId, journalBlobName, snapshotId));
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        return blobName;
    }

    /// <summary>
    /// A function for building container factory instances.
    /// </summary>
    public Func<IServiceProvider, AzureBlobJournalStorageOptions, IBlobContainerFactory> BuildContainerFactory { get; set; }
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

/// <summary>
/// Context used to generate an Azure Blob journal snapshot name.
/// </summary>
public sealed class AzureBlobJournalSnapshotNameContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureBlobJournalSnapshotNameContext"/> class.
    /// </summary>
    /// <param name="grainId">The grain id associated with the journal.</param>
    /// <param name="journalBlobName">The append blob name used for the journal marker and tail.</param>
    /// <param name="snapshotId">A generated id which is unique to the current compaction attempt.</param>
    public AzureBlobJournalSnapshotNameContext(GrainId grainId, string journalBlobName, string snapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalBlobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        GrainId = grainId;
        JournalBlobName = journalBlobName;
        SnapshotId = snapshotId;
    }

    /// <summary>
    /// Gets the grain id associated with the journal.
    /// </summary>
    public GrainId GrainId { get; }

    /// <summary>
    /// Gets the append blob name used for the journal marker and tail.
    /// </summary>
    public string JournalBlobName { get; }

    /// <summary>
    /// Gets a generated id which is unique to the current compaction attempt.
    /// </summary>
    public string SnapshotId { get; }
}
