using Azure;
using Azure.Core;
using Azure.Data.Tables;

namespace Orleans.Journaling;

/// <summary>
/// Options for configuring the Azure Table journal storage provider.
/// </summary>
public sealed class AzureTableJournalStorageOptions
{
    /// <summary>
    /// Table name where journal data is stored.
    /// </summary>
    public string TableName { get; set; } = DEFAULT_TABLE_NAME;
    public const string DEFAULT_TABLE_NAME = "journal";

    /// <summary>
    /// Gets or sets the delegate used to generate the table partition key for a journal.
    /// </summary>
    /// <remarks>
    /// The returned value must be a valid Azure Table partition key. The default value percent-encodes
    /// <see cref="JournalId.Value"/> reversibly and rejects values whose encoded form exceeds the Azure
    /// Table partition-key limit.
    /// </remarks>
    public Func<JournalId, string> GetPartitionKey { get; set; } = DefaultGetPartitionKey;

    private static readonly Func<JournalId, string> DefaultGetPartitionKey =
        static journalId => GetDefaultPartitionKey(journalId);

    /// <summary>
    /// Options to be used when configuring the table storage client, or <see langword="null"/> to use the default options.
    /// </summary>
    public TableClientOptions? ClientOptions { get; set; }

    /// <summary>
    /// Gets or sets the client used to access the Azure Table Service.
    /// </summary>
    public TableServiceClient? TableServiceClient
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
    /// Gets or sets a value indicating whether rows belonging to the previous journal generation are deleted
    /// after a replace operation publishes a new generation. Defaults to true.
    /// </summary>
    public bool DeleteOldGenerations { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of data rows written since the last replace operation at which
    /// <see cref="IJournalStorage.IsCompactionRequested"/> becomes true. Defaults to 10,000.
    /// </summary>
    public long CompactionRowCountThreshold { get; set; } = DEFAULT_COMPACTION_ROW_COUNT_THRESHOLD;
    public const long DEFAULT_COMPACTION_ROW_COUNT_THRESHOLD = 10_000;

    /// <summary>
    /// Gets or sets the number of journal bytes written since the last replace operation at which
    /// <see cref="IJournalStorage.IsCompactionRequested"/> becomes true. Defaults to 32 MiB.
    /// </summary>
    public long CompactionSizeThreshold { get; set; } = DEFAULT_COMPACTION_SIZE_THRESHOLD;
    public const long DEFAULT_COMPACTION_SIZE_THRESHOLD = 32L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum number of times a journaling Append, Replace, or Delete operation
    /// will refresh its cached header ETag and retry in place after observing a metadata-only ETag
    /// conflict (for example, a concurrent caller-owned metadata update).
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
    /// The optional delegate used to create a <see cref="TableServiceClient"/> instance.
    /// </summary>
    internal Func<CancellationToken, Task<TableServiceClient>>? CreateClient { get; private set; }

    internal string GetPartitionKeyForJournal(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        var mapper = GetPartitionKey ?? throw new InvalidOperationException("A partition key mapper must be configured.");
        var partitionKey = mapper(journalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ValidatePartitionKey(partitionKey, nameof(partitionKey));
        return partitionKey;
    }

    internal static string GetDefaultPartitionKey(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        // Percent-encoding escapes every character disallowed in partition keys ('/', '\', '#', '?',
        // control characters) and is reversible.
        var partitionKey = Uri.EscapeDataString(journalId.Value);
        ValidatePartitionKey(partitionKey, nameof(journalId));
        return partitionKey;
    }

    internal static void ValidateTableName(string? tableName)
    {
        if (tableName is not { Length: >= 3 and <= 63 } || !IsAsciiLetter(tableName[0]))
        {
#pragma warning disable CA2208 // Validation reports the public configuration property rather than this helper parameter.
            throw new ArgumentException(
                "Azure Table names must contain 3 to 63 alphanumeric characters and begin with a letter.",
                nameof(TableName));
#pragma warning restore CA2208
        }

        foreach (var character in tableName)
        {
            if (!IsAsciiLetter(character) && character is not (>= '0' and <= '9'))
            {
#pragma warning disable CA2208 // Validation reports the public configuration property rather than this helper parameter.
                throw new ArgumentException(
                    "Azure Table names must contain 3 to 63 alphanumeric characters and begin with a letter.",
                    nameof(TableName));
#pragma warning restore CA2208
            }
        }
    }

    private static void ValidatePartitionKey(string partitionKey, string parameterName)
    {
        if (partitionKey.Length > 1024)
        {
            throw new ArgumentException(
                "Azure Table partition keys must not exceed 1,024 characters.",
                parameterName);
        }

        foreach (var character in partitionKey)
        {
            if (character is '/' or '\\' or '#' or '?'
                or >= '\u0000' and <= '\u001F'
                or >= '\u007F' and <= '\u009F')
            {
                throw new ArgumentException(
                    "Azure Table partition keys must not contain '/', '\\', '#', '?', or control characters.",
                    parameterName);
            }
        }
    }

    private static bool IsAsciiLetter(char character)
        => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    /// <summary>
    /// Configures the <see cref="TableServiceClient"/> using a connection string.
    /// </summary>
    public void ConfigureTableServiceClient(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        CreateClient = ct => Task.FromResult(new TableServiceClient(connectionString, ClientOptions));
    }

    /// <summary>
    /// Configures the <see cref="TableServiceClient"/> using an authenticated service URI.
    /// </summary>
    public void ConfigureTableServiceClient(Uri serviceUri)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        CreateClient = ct => Task.FromResult(new TableServiceClient(serviceUri, ClientOptions));
    }

    /// <summary>
    /// Configures the <see cref="TableServiceClient"/> using the provided callback.
    /// </summary>
    public void ConfigureTableServiceClient(Func<CancellationToken, Task<TableServiceClient>> createClientCallback)
    {
        CreateClient = createClientCallback ?? throw new ArgumentNullException(nameof(createClientCallback));
    }

    /// <summary>
    /// Configures the <see cref="TableServiceClient"/> using an authenticated service URI and a <see cref="TokenCredential"/>.
    /// </summary>
    public void ConfigureTableServiceClient(Uri serviceUri, TokenCredential tokenCredential)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        ArgumentNullException.ThrowIfNull(tokenCredential);
        CreateClient = ct => Task.FromResult(new TableServiceClient(serviceUri, tokenCredential, ClientOptions));
    }

    /// <summary>
    /// Configures the <see cref="TableServiceClient"/> using an authenticated service URI and a <see cref="AzureSasCredential"/>.
    /// </summary>
    public void ConfigureTableServiceClient(Uri serviceUri, AzureSasCredential azureSasCredential)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        ArgumentNullException.ThrowIfNull(azureSasCredential);
        CreateClient = ct => Task.FromResult(new TableServiceClient(serviceUri, azureSasCredential, ClientOptions));
    }

    /// <summary>
    /// Configures the <see cref="TableServiceClient"/> using an authenticated service URI and a <see cref="TableSharedKeyCredential"/>.
    /// </summary>
    public void ConfigureTableServiceClient(Uri serviceUri, TableSharedKeyCredential sharedKeyCredential)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);
        ArgumentNullException.ThrowIfNull(sharedKeyCredential);
        CreateClient = ct => Task.FromResult(new TableServiceClient(serviceUri, sharedKeyCredential, ClientOptions));
    }
}
