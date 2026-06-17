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
    /// Gets or sets the delegate used to generate the write-ahead log blob name for a journal.
    /// </summary>
    public Func<JournalId, string> GetWalBlobName { get; set; } = DefaultGetWalBlobName;

    private static readonly Func<JournalId, string> DefaultGetWalBlobName =
        static journalId => GetDefaultWalBlobName(journalId);

    /// <summary>
    /// Gets or sets the delegate used to generate the checkpoint blob name for a journal snapshot.
    /// </summary>
    /// <remarks>
    /// The delegate receives the journal id and an opaque snapshot id generated for the checkpoint.
    /// The snapshot id is currently formatted as a 32-character hexadecimal string using <c>Guid.ToString("N")</c>.
    /// The returned value must be a container-relative blob name. The default value is
    /// <c>{journalId.Value}/chk.{snapshotId}</c>.
    /// </remarks>
    public Func<JournalId, string, string> GetCheckpointBlobName { get; set; } = DefaultGetCheckpointBlobName;

    private static readonly Func<JournalId, string, string> DefaultGetCheckpointBlobName =
        static (journalId, snapshotId) => GetDefaultCheckpointBlobName(journalId, snapshotId);

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
    /// Gets or sets the maximum number of times a journaling Append, Replace, or Delete operation
    /// will refresh its cached WAL ETag and retry in place after observing a metadata-only ETag
    /// conflict (for example, a concurrent caller-owned metadata update or generation bump).
    /// When the cap is exceeded the storage layer falls back to throwing
    /// <see cref="Orleans.Storage.InconsistentStateException"/> and the journaling layer recovers
    /// before retrying. Defaults to 5.
    /// </summary>
    public int MaxMetadataOnlyConflictRetries { get; set; } = DEFAULT_MAX_METADATA_ONLY_CONFLICT_RETRIES;
    public const int DEFAULT_MAX_METADATA_ONLY_CONFLICT_RETRIES = 5;

    /// <summary>
    /// Gets or sets the initial delay applied before re-trying after a metadata-only ETag conflict.
    /// Subsequent attempts double the delay (capped at <see cref="MetadataOnlyConflictMaxBackoff"/>).
    /// Set to <see cref="TimeSpan.Zero"/> to retry immediately without backoff. Defaults to 10 ms.
    /// </summary>
    public TimeSpan MetadataOnlyConflictInitialBackoff { get; set; } = DEFAULT_METADATA_ONLY_CONFLICT_INITIAL_BACKOFF;
    public static readonly TimeSpan DEFAULT_METADATA_ONLY_CONFLICT_INITIAL_BACKOFF = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Gets or sets the upper bound on the per-attempt backoff used by metadata-only conflict
    /// retries. The exponential schedule starts at <see cref="MetadataOnlyConflictInitialBackoff"/>
    /// and never exceeds this value. Defaults to 200 ms.
    /// </summary>
    public TimeSpan MetadataOnlyConflictMaxBackoff { get; set; } = DEFAULT_METADATA_ONLY_CONFLICT_MAX_BACKOFF;
    public static readonly TimeSpan DEFAULT_METADATA_ONLY_CONFLICT_MAX_BACKOFF = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The optional delegate used to create a <see cref="BlobServiceClient"/> instance.
    /// </summary>
    internal Func<CancellationToken, Task<BlobServiceClient>>? CreateClient { get; private set; }

    internal string GetWalBlobNameForJournal(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        var blobName = GetWalBlobName(journalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        return blobName;
    }

    internal string GetCheckpointBlobNameForJournal(JournalId journalId, string snapshotId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        var blobName = GetCheckpointBlobName(journalId, snapshotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        return blobName;
    }

    internal static string GetDefaultWalBlobName(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        return $"{journalId.Value}/wal";
    }

    internal static string GetDefaultCheckpointBlobName(JournalId journalId, string snapshotId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        return $"{journalId.Value}/chk.{snapshotId}";
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
