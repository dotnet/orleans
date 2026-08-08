using Amazon;
using Amazon.Runtime;
using Amazon.S3;

namespace Orleans.Journaling;

/// <summary>
/// Options for configuring the Amazon S3 journal storage provider.
/// </summary>
public sealed class S3JournalStorageOptions
{
    private IAmazonS3? _s3Client;

    /// <summary>
    /// Bucket name where journals are stored.
    /// </summary>
    public string? BucketName { get; set; }

    /// <summary>
    /// Gets or sets the delegate used to generate the base object key for a journal.
    /// </summary>
    public Func<JournalId, string> GetObjectKey { get; set; } = DefaultGetObjectKey;

    /// <summary>
    /// Gets or sets the delegate used to parse journal ids from catalog object keys.
    /// </summary>
    public Func<string, JournalId?> TryParseJournalId { get; set; } = DefaultTryParseJournalId;

    /// <summary>
    /// Options to use when creating the S3 client, or <see langword="null"/> to use the AWS SDK defaults.
    /// </summary>
    public AmazonS3Config? ClientConfig { get; set; }

    /// <summary>
    /// Gets or sets the client used to access S3.
    /// </summary>
    public IAmazonS3? S3Client
    {
        get => _s3Client;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _s3Client = value;
            CreateClient = _ => Task.FromResult(value);
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the configured bucket should be created if it does not exist.
    /// </summary>
    /// <remarks>
    /// This is intended for local emulators. AWS S3 Express One Zone directory buckets should generally be provisioned ahead of time.
    /// </remarks>
    public bool CreateBucketIfNotExists { get; set; }

    /// <summary>
    /// Gets or sets the S3 storage class applied to newly-created WAL and checkpoint objects.
    /// </summary>
    public S3StorageClass? StorageClass { get; set; } = S3StorageClass.ExpressOnezone;

    /// <summary>
    /// Gets or sets a value indicating whether appends use S3 Express <see cref="Amazon.S3.Model.PutObjectRequest.WriteOffsetBytes"/>.
    /// </summary>
    /// <remarks>
    /// Set this to <see langword="false"/> for S3-compatible emulators such as MinIO which do not support S3 Express append writes.
    /// </remarks>
    public bool UseS3ExpressAppend { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether delete requests use S3 Express conditional delete headers.
    /// </summary>
    public bool UseConditionalDelete { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether obsolete checkpoint objects are deleted after a new checkpoint is published. Defaults to true.
    /// </summary>
    public bool DeleteOldCheckpoints { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of times Append, Replace, or Delete will refresh its cached WAL ETag
    /// and retry in place after observing a metadata-only conflict. Defaults to 5.
    /// </summary>
    public int MaxMetadataOnlyConflictRetries { get; set; } = DEFAULT_MAX_METADATA_ONLY_CONFLICT_RETRIES;
    public const int DEFAULT_MAX_METADATA_ONLY_CONFLICT_RETRIES = 5;

    /// <summary>
    /// Gets or sets the initial delay applied before retrying after a metadata-only conflict. Defaults to 10 ms.
    /// </summary>
    public TimeSpan MetadataOnlyConflictInitialBackoff { get; set; } = DEFAULT_METADATA_ONLY_CONFLICT_INITIAL_BACKOFF;
    public static readonly TimeSpan DEFAULT_METADATA_ONLY_CONFLICT_INITIAL_BACKOFF = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Gets or sets the upper bound on metadata-only conflict retry backoff. Defaults to 200 ms.
    /// </summary>
    public TimeSpan MetadataOnlyConflictMaxBackoff { get; set; } = DEFAULT_METADATA_ONLY_CONFLICT_MAX_BACKOFF;
    public static readonly TimeSpan DEFAULT_METADATA_ONLY_CONFLICT_MAX_BACKOFF = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The optional delegate used to create an <see cref="IAmazonS3"/> instance.
    /// </summary>
    internal Func<CancellationToken, Task<IAmazonS3>>? CreateClient { get; private set; }

    internal string GetObjectKeyForJournal(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        var objectKey = GetObjectKey(journalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        return objectKey;
    }

    internal static string GetWalObjectKeyForJournal(JournalId journalId, string journalObjectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalObjectKey);
        return GetDefaultWalObjectKey(journalObjectKey);
    }

    internal static string GetCheckpointObjectKeyForJournal(JournalId journalId, string journalObjectKey, string snapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalObjectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        return GetDefaultCheckpointObjectKey(journalObjectKey, snapshotId);
    }

    internal static string GetDefaultWalObjectKey(string journalObjectKey) => $"{journalObjectKey}/wal";

    internal static string GetDefaultCheckpointObjectKey(string journalObjectKey, string snapshotId) => $"{journalObjectKey}/chk.{snapshotId}";

    internal Func<CancellationToken, Task<IAmazonS3>> GetCreateClient()
        => CreateClient ?? (_ => Task.FromResult<IAmazonS3>(new AmazonS3Client(ClientConfig ?? new AmazonS3Config())));

    /// <summary>
    /// Configures the S3 client using the provided callback.
    /// </summary>
    public void ConfigureS3Client(Func<CancellationToken, Task<IAmazonS3>> createClientCallback)
        => CreateClient = createClientCallback ?? throw new ArgumentNullException(nameof(createClientCallback));

    /// <summary>
    /// Configures the S3 client using the AWS SDK default credential chain and the provided client configuration.
    /// </summary>
    public void ConfigureS3Client(AmazonS3Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ClientConfig = config;
        CreateClient = _ => Task.FromResult<IAmazonS3>(new AmazonS3Client(config));
    }

    /// <summary>
    /// Configures the S3 client using the AWS SDK default credential chain and the provided region.
    /// </summary>
    public void ConfigureS3Client(RegionEndpoint regionEndpoint)
    {
        ArgumentNullException.ThrowIfNull(regionEndpoint);
        ConfigureS3Client(new AmazonS3Config { RegionEndpoint = regionEndpoint });
    }

    /// <summary>
    /// Configures the S3 client using explicit credentials and client configuration.
    /// </summary>
    public void ConfigureS3Client(string accessKey, string secretKey, AmazonS3Config config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        ArgumentNullException.ThrowIfNull(config);
        ClientConfig = config;
        var credentials = new BasicAWSCredentials(accessKey, secretKey);
        CreateClient = _ => Task.FromResult<IAmazonS3>(new AmazonS3Client(credentials, config));
    }

    private static string DefaultGetObjectKey(JournalId journalId) => journalId.Value;

    private static JournalId? DefaultTryParseJournalId(string value)
    {
        try
        {
            return new JournalId(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
