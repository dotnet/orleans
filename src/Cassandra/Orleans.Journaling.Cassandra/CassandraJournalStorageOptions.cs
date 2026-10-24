using Cassandra;

namespace Orleans.Journaling;

/// <summary>
/// Options for configuring Cassandra journal storage.
/// </summary>
public sealed class CassandraJournalStorageOptions
{
    private static readonly Guid HeaderGeneration = Guid.Empty;
    private Func<IServiceProvider, Task<ISession>>? _createSession;

    /// <summary>
    /// Gets or sets the Cassandra table name.
    /// </summary>
    public string TableName { get; set; } = "orleans_journal";

    /// <summary>
    /// Gets or sets the maximum number of bytes written by one append operation.
    /// </summary>
    public int MaxAppendBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the size of each journal row in bytes.
    /// </summary>
    public int RowSizeBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Gets or sets the maximum number of appends retained before storage requests compaction.
    /// </summary>
    public int CompactionAppendCount { get; set; } = 100;

    /// <summary>
    /// Gets or sets the number of appended bytes after which storage requests compaction.
    /// </summary>
    public long CompactionThresholdBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>
    /// Gets or sets the Cassandra page size used when reading journal rows.
    /// </summary>
    public int ReadPageSize { get; set; } = 128;

    internal ConsistencyLevel ReadConsistencyLevel { get; set; } = ConsistencyLevel.Quorum;
    internal ConsistencyLevel WriteConsistencyLevel { get; set; } = ConsistencyLevel.Quorum;
    internal ConsistencyLevel SerialConsistencyLevel { get; set; } = ConsistencyLevel.Serial;
    internal Func<IServiceProvider, Task<ISession>> CreateSessionAsync
        => _createSession ?? throw new InvalidOperationException("Configure a Cassandra session before starting the silo.");
    internal bool OwnsSession { get; private set; }

    /// <summary>
    /// Configures the Cassandra client using a connection string.
    /// The provider creates and owns the resulting cluster and session.
    /// </summary>
    /// <param name="connectionString">The Cassandra connection string.</param>
    /// <param name="keyspace">The keyspace to use.</param>
    public void ConfigureClient(string connectionString, string keyspace = "orleans")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);
        OwnsSession = true;
        _createSession = async _ =>
        {
            var cluster = Cluster.Builder().WithConnectionString(connectionString).Build();
            try
            {
                return await cluster.ConnectAsync(keyspace).ConfigureAwait(false);
            }
            catch
            {
                cluster.Dispose();
                throw;
            }
        };
    }

    /// <summary>
    /// Configures the Cassandra session factory.
    /// The caller retains ownership of the returned session.
    /// </summary>
    /// <param name="sessionFactory">Creates the session used by the provider.</param>
    public void ConfigureClient(Func<IServiceProvider, Task<ISession>> sessionFactory)
    {
        _createSession = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        OwnsSession = false;
    }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TableName);
        if (!IsValidIdentifier(TableName))
        {
            throw new ArgumentException("The Cassandra table name must be a simple CQL identifier.", nameof(TableName));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxAppendBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RowSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CompactionAppendCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CompactionThresholdBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReadPageSize);
    }

    internal static Guid HeaderGenerationId => HeaderGeneration;

    private static bool IsValidIdentifier(string value)
    {
        if (!(value[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_'))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (!(value[i] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
