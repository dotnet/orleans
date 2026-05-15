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
    /// Gets or sets the delegate used to generate append blob names for journal WAL segments.
    /// </summary>
    public Func<AzureBlobJournalWalSegmentNameContext, string> GetWalSegmentBlobName { get; set; } = DefaultGetWalSegmentBlobName;

    private static readonly Func<AzureBlobJournalWalSegmentNameContext, string> DefaultGetWalSegmentBlobName =
        static context => $"{context.JournalBlobName}.log.{context.SegmentId:X8}";

    /// <summary>
    /// Gets or sets the delegate used to generate block blob names for immutable journal checkpoints.
    /// </summary>
    public Func<AzureBlobJournalCheckpointNameContext, string> GetCheckpointBlobName { get; set; } = DefaultGetCheckpointBlobName;

    private static readonly Func<AzureBlobJournalCheckpointNameContext, string> DefaultGetCheckpointBlobName =
        static context => $"{context.JournalBlobName}.checkpoint.{context.CheckpointId:X8}";

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

    internal string GetWalSegmentBlobNameForJournal(GrainId grainId, string journalBlobName, uint segmentId)
    {
        var blobName = GetWalSegmentBlobName(new AzureBlobJournalWalSegmentNameContext(grainId, journalBlobName, segmentId));
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        return blobName;
    }

    internal string GetCheckpointBlobNameForJournal(GrainId grainId, string journalBlobName, uint checkpointId)
    {
        var blobName = GetCheckpointBlobName(new AzureBlobJournalCheckpointNameContext(grainId, journalBlobName, checkpointId));
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
/// Context used to generate an Azure Blob journal WAL segment name.
/// </summary>
public sealed class AzureBlobJournalWalSegmentNameContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureBlobJournalWalSegmentNameContext"/> class.
    /// </summary>
    /// <param name="grainId">The grain id associated with the journal.</param>
    /// <param name="journalBlobName">The base blob name used for the journal.</param>
    /// <param name="segmentId">The WAL segment id.</param>
    public AzureBlobJournalWalSegmentNameContext(GrainId grainId, string journalBlobName, uint segmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalBlobName);

        GrainId = grainId;
        JournalBlobName = journalBlobName;
        SegmentId = segmentId;
    }

    /// <summary>
    /// Gets the grain id associated with the journal.
    /// </summary>
    public GrainId GrainId { get; }

    /// <summary>
    /// Gets the base blob name used for the journal.
    /// </summary>
    public string JournalBlobName { get; }

    /// <summary>
    /// Gets the WAL segment id.
    /// </summary>
    public uint SegmentId { get; }
}

/// <summary>
/// Context used to generate an Azure Blob journal checkpoint name.
/// </summary>
public sealed class AzureBlobJournalCheckpointNameContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureBlobJournalCheckpointNameContext"/> class.
    /// </summary>
    /// <param name="grainId">The grain id associated with the journal.</param>
    /// <param name="journalBlobName">The base blob name used for the journal.</param>
    /// <param name="checkpointId">The checkpoint id.</param>
    public AzureBlobJournalCheckpointNameContext(GrainId grainId, string journalBlobName, uint checkpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalBlobName);

        GrainId = grainId;
        JournalBlobName = journalBlobName;
        CheckpointId = checkpointId;
    }

    /// <summary>
    /// Gets the grain id associated with the journal.
    /// </summary>
    public GrainId GrainId { get; }

    /// <summary>
    /// Gets the base blob name used for the journal.
    /// </summary>
    public string JournalBlobName { get; }

    /// <summary>
    /// Gets the checkpoint id.
    /// </summary>
    public uint CheckpointId { get; }
}
