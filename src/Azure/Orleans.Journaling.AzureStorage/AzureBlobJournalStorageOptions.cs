using Azure;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Blobs;

namespace Orleans.Journaling;

/// <summary>
/// Options for configuring the Azure Blob state storage provider.
/// </summary>
public sealed class AzureBlobJournalStorageOptions
{
    /// <summary>
    /// Container name where state is stored.
    /// </summary>
    public string ContainerName { get; set; } = DEFAULT_CONTAINER_NAME;
    public const string DEFAULT_CONTAINER_NAME = "state";

    /// <summary>
    /// Gets or sets the delegate used to generate the blob name for a journal.
    /// </summary>
    public Func<JournalId, string> GetBlobName { get; set; } = DefaultGetBlobName;

    private static readonly Func<JournalId, string> DefaultGetBlobName = static journalId => journalId.Value;

    /// <summary>
    /// Options to be used when configuring the blob storage client, or <see langword="null"/> to use the default options.
    /// </summary>
    public BlobClientOptions? ClientOptions { get; set; }

    /// <summary>
    /// Gets or sets the client used to access the Azure Blob Service.
    /// </summary>
    public BlobServiceClient? BlobServiceClient
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
            CreateClient = ct => Task.FromResult(value);
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether obsolete checkpoint blobs are deleted after a new checkpoint is published. Defaults to true.
    /// </summary>
    public bool DeleteOldCheckpoints { get; set; } = true;

    /// <summary>
    /// The optional delegate used to create a <see cref="BlobServiceClient"/> instance.
    /// </summary>
    internal Func<CancellationToken, Task<BlobServiceClient>>? CreateClient { get; private set; }

    internal string GetBlobNameForJournal(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        var blobName = GetBlobName(journalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        return blobName;
    }

    internal static string GetWalBlobNameForJournal(JournalId journalId, string journalBlobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalBlobName);
        return GetDefaultWalBlobName(journalBlobName);
    }

    internal static string GetCheckpointBlobNameForJournal(JournalId journalId, string journalBlobName, string snapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalBlobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        return GetDefaultCheckpointBlobName(journalBlobName, snapshotId);
    }

    internal static string GetDefaultWalBlobName(string journalBlobName)
        => $"{journalBlobName}/wal";

    internal static string GetDefaultCheckpointBlobName(string journalBlobName, string snapshotId)
        => $"{journalBlobName}/chk.{snapshotId}";

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
