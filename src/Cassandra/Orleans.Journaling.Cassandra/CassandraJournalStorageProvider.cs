using Cassandra;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Journaling;

internal sealed class CassandraJournalStorageProvider : IJournalStorageProvider, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly CassandraJournalStorageOptions _options;
    private readonly string _journalFormatKey;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CassandraJournalStorage> _storageLogger;
    private CassandraJournalStorageQueries? _queries;
    private ISession? _session;

    public CassandraJournalStorageProvider(
        IOptions<CassandraJournalStorageOptions> options,
        IOptions<JournaledStateManagerOptions> managerOptions,
        IServiceProvider serviceProvider,
        ILogger<CassandraJournalStorage> storageLogger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(managerOptions);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        _options = options.Value;
        _options.Validate();
        _journalFormatKey = managerOptions.Value.JournalFormatKey;
        if (string.IsNullOrWhiteSpace(_journalFormatKey))
        {
            throw new InvalidOperationException("The configured journal format key must be non-empty.");
        }

        _serviceProvider = serviceProvider;
        _storageLogger = storageLogger;
    }

    public IJournalStorage CreateStorage(JournalId journalId)
    {
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        var queries = _queries
            ?? throw new InvalidOperationException(
                $"{nameof(CassandraJournalStorageProvider)} has not been initialized. Ensure the silo lifecycle has started before using journal storage.");
        return new CassandraJournalStorage(queries, _options, journalId, _journalFormatKey, _storageLogger);
    }

    public void Participate(ISiloLifecycle observer)
    {
        observer.Subscribe(
            nameof(CassandraJournalStorageProvider),
            ServiceLifecycleStage.RuntimeInitialize,
            onStart: InitializeAsync,
            onStop: CloseAsync);
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = await _options.CreateSessionAsync(_serviceProvider).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The configured Cassandra session factory returned null.");
        try
        {
            var queries = new CassandraJournalStorageQueries(session, _options);
            await queries.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _session = session;
            _queries = queries;
        }
        catch
        {
            if (_options.OwnsSession)
            {
                session.Cluster.Dispose();
            }

            throw;
        }
    }

    internal Task CloseAsync(CancellationToken cancellationToken)
    {
        var session = _session;
        _session = null;
        _queries = null;
        if (session is not null && _options.OwnsSession)
        {
            session.Cluster.Dispose();
        }

        return Task.CompletedTask;
    }
}

internal sealed class CassandraJournalStorageQueries
{
    private readonly string _tableName;
    private readonly CassandraJournalStorageOptions _options;
    private readonly ISession _session;

    private PreparedStatement? _createHeader;
    private PreparedStatement? _readHeader;
    private PreparedStatement? _readEntries;
    private PreparedStatement? _appendEntry;
    private PreparedStatement? _appendHeader;
    private PreparedStatement? _replaceEntry;
    private PreparedStatement? _publishReplacement;
    private PreparedStatement? _updateMetadata;
    private PreparedStatement? _deleteHeader;
    private PreparedStatement? _deleteGeneration;
    private PreparedStatement? _deleteOlderGenerations;

    public CassandraJournalStorageQueries(ISession session, CassandraJournalStorageOptions options)
    {
        _session = session;
        _options = options;
        _tableName = options.TableName;
    }

    public ISession Session => _session;
    public CassandraJournalStorageOptions Options => _options;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var createTable = new SimpleStatement($"""
            CREATE TABLE IF NOT EXISTS {_tableName} (
                journal_id text,
                generation uuid,
                sequence bigint,
                active_generation uuid,
                next_sequence bigint,
                total_length bigint,
                append_count bigint,
                append_length bigint,
                journal_format text,
                caller_metadata map<text, text>,
                revision uuid,
                content_revision uuid,
                schema_version int,
                operation_id uuid,
                payload blob,
                PRIMARY KEY ((journal_id), generation, sequence)
            ) WITH CLUSTERING ORDER BY (generation ASC, sequence ASC)
            """).SetConsistencyLevel(_options.WriteConsistencyLevel);
        await _session.ExecuteAsync(createTable).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        _createHeader = await PrepareAsync($"""
            INSERT INTO {_tableName} (
                journal_id, generation, sequence, active_generation, next_sequence, total_length,
                append_count, append_length, journal_format, caller_metadata, revision, content_revision,
                schema_version)
            VALUES (?, ?, ?, ?, 0, 0, 0, 0, ?, ?, ?, ?, 1)
            IF NOT EXISTS
            """, cancellationToken).ConfigureAwait(false);
        _readHeader = await PrepareAsync($"""
            SELECT active_generation, next_sequence, total_length, append_count, append_length,
                journal_format, caller_metadata, revision, content_revision, schema_version
            FROM {_tableName} WHERE journal_id = ? AND generation = ? AND sequence = ?
            """, cancellationToken).ConfigureAwait(false);
        _readEntries = await PrepareAsync($"""
            SELECT sequence, payload FROM {_tableName}
            WHERE journal_id = ? AND generation = ? AND sequence >= 0 AND sequence < ?
            """, cancellationToken).ConfigureAwait(false);
        _appendEntry = await PrepareAsync($"""
            INSERT INTO {_tableName} (journal_id, generation, sequence, operation_id, payload)
            VALUES (?, ?, ?, ?, ?) IF NOT EXISTS
            """, cancellationToken).ConfigureAwait(false);
        _appendHeader = await PrepareAsync($"""
            UPDATE {_tableName}
            SET journal_format = ?, next_sequence = ?, total_length = ?, append_count = ?, append_length = ?,
                revision = ?, content_revision = ?
            WHERE journal_id = ? AND generation = ? AND sequence = ?
            IF revision = ? AND active_generation = ? AND next_sequence = ?
            """, cancellationToken).ConfigureAwait(false);
        _replaceEntry = await PrepareAsync($"""
            INSERT INTO {_tableName} (journal_id, generation, sequence, operation_id, payload)
            VALUES (?, ?, ?, ?, ?)
            """, cancellationToken).ConfigureAwait(false);
        _publishReplacement = await PrepareAsync($"""
            UPDATE {_tableName}
            SET active_generation = ?, next_sequence = ?, total_length = ?, append_count = 0,
                append_length = 0, journal_format = ?, revision = ?, content_revision = ?
            WHERE journal_id = ? AND generation = ? AND sequence = ?
            IF revision = ? AND active_generation = ? AND next_sequence = ?
            """, cancellationToken).ConfigureAwait(false);
        _updateMetadata = await PrepareAsync($"""
            UPDATE {_tableName}
            SET caller_metadata = ?, revision = ?
            WHERE journal_id = ? AND generation = ? AND sequence = ?
            IF revision = ?
            """, cancellationToken).ConfigureAwait(false);
        _deleteHeader = await PrepareAsync($"""
            DELETE FROM {_tableName}
            WHERE journal_id = ? AND generation = ? AND sequence = ?
            IF revision = ?
            """, cancellationToken).ConfigureAwait(false);
        _deleteGeneration = await PrepareAsync($"""
            DELETE FROM {_tableName}
            WHERE journal_id = ? AND generation = ?
            """, cancellationToken).ConfigureAwait(false);
        _deleteOlderGenerations = await PrepareAsync($"""
            DELETE FROM {_tableName}
            WHERE journal_id = ? AND generation > ? AND generation < ?
            """, cancellationToken).ConfigureAwait(false);
    }

    public BoundStatement CreateHeader(string journalId, Guid generation, string format, Dictionary<string, string> metadata, Guid revision, Guid contentRevision)
    {
        var statement = Get(_createHeader).Bind(journalId, CassandraJournalStorageOptions.HeaderGenerationId, -1L, generation, format, metadata, revision, contentRevision);
        statement.SetConsistencyLevel(_options.WriteConsistencyLevel);
        statement.SetSerialConsistencyLevel(_options.SerialConsistencyLevel);
        return statement;
    }

    public BoundStatement ReadHeader(string journalId)
    {
        var statement = Get(_readHeader).Bind(journalId, CassandraJournalStorageOptions.HeaderGenerationId, -1L);
        // SERIAL here requests a serial read, which completes any pending Paxos round for this partition.
        statement.SetConsistencyLevel(_options.SerialConsistencyLevel);
        return statement;
    }

    public BoundStatement ReadEntries(string journalId, Guid generation, long nextSequence)
    {
        var statement = Get(_readEntries).Bind(journalId, generation, nextSequence);
        statement.SetConsistencyLevel(_options.ReadConsistencyLevel);
        statement.SetPageSize(_options.ReadPageSize);
        return statement;
    }

    public BatchStatement Append(string journalId, Guid generation, long sequence, Guid operationId, byte[] payload,
        long newNextSequence, long totalLength, long appendCount, long appendLength, string format, Guid revision, Guid contentRevision,
        Guid expectedRevision, long expectedNextSequence)
    {
        var batch = new BatchStatement();
        batch.SetBatchType(BatchType.Logged);
        var entry = Get(_appendEntry).Bind(journalId, generation, sequence, operationId, payload);
        entry.SetConsistencyLevel(_options.WriteConsistencyLevel);
        entry.SetSerialConsistencyLevel(_options.SerialConsistencyLevel);
        batch.Add(entry);
        var header = Get(_appendHeader).Bind(
            format, newNextSequence, totalLength, appendCount, appendLength, revision, contentRevision,
            journalId, CassandraJournalStorageOptions.HeaderGenerationId, -1L,
            expectedRevision, generation, expectedNextSequence);
        header.SetConsistencyLevel(_options.WriteConsistencyLevel);
        header.SetSerialConsistencyLevel(_options.SerialConsistencyLevel);
        batch.Add(header);
        batch.SetConsistencyLevel(_options.WriteConsistencyLevel);
        batch.SetSerialConsistencyLevel(_options.SerialConsistencyLevel);
        return batch;
    }

    public BoundStatement ReplaceEntry(string journalId, Guid generation, long sequence, Guid operationId, byte[] payload)
    {
        var statement = Get(_replaceEntry).Bind(journalId, generation, sequence, operationId, payload);
        statement.SetConsistencyLevel(_options.WriteConsistencyLevel);
        return statement;
    }

    public BoundStatement PublishReplacement(string journalId, Guid generation, long nextSequence, long totalLength,
        string format, Guid revision, Guid contentRevision, Guid expectedRevision, Guid expectedGeneration, long expectedNextSequence)
    {
        var statement = Get(_publishReplacement).Bind(
            generation, nextSequence, totalLength, format, revision, contentRevision,
            journalId, CassandraJournalStorageOptions.HeaderGenerationId, -1L,
            expectedRevision, expectedGeneration, expectedNextSequence);
        statement.SetConsistencyLevel(_options.WriteConsistencyLevel);
        statement.SetSerialConsistencyLevel(_options.SerialConsistencyLevel);
        return statement;
    }

    public BoundStatement UpdateMetadata(string journalId, Dictionary<string, string> metadata, Guid revision, Guid expectedRevision)
    {
        var statement = Get(_updateMetadata).Bind(metadata, revision, journalId, CassandraJournalStorageOptions.HeaderGenerationId, -1L, expectedRevision);
        statement.SetConsistencyLevel(_options.WriteConsistencyLevel);
        statement.SetSerialConsistencyLevel(_options.SerialConsistencyLevel);
        return statement;
    }

    public BoundStatement DeleteHeader(string journalId, Guid expectedRevision)
    {
        var statement = Get(_deleteHeader).Bind(journalId, CassandraJournalStorageOptions.HeaderGenerationId, -1L, expectedRevision);
        statement.SetConsistencyLevel(_options.WriteConsistencyLevel);
        statement.SetSerialConsistencyLevel(_options.SerialConsistencyLevel);
        return statement;
    }

    public BoundStatement DeleteGeneration(string journalId, Guid generation)
    {
        var statement = Get(_deleteGeneration).Bind(journalId, generation);
        statement.SetConsistencyLevel(_options.WriteConsistencyLevel);
        return statement;
    }

    public BoundStatement DeleteOlderGenerations(string journalId, Guid cutoff)
    {
        var statement = Get(_deleteOlderGenerations).Bind(journalId, CassandraJournalStorageOptions.HeaderGenerationId, cutoff);
        statement.SetConsistencyLevel(_options.WriteConsistencyLevel);
        return statement;
    }

    public static bool WasApplied(RowSet result)
    {
        var row = result.FirstOrDefault();
        return row is not null && row.GetValue<bool>("[applied]");
    }

    private async Task<PreparedStatement> PrepareAsync(string query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _session.PrepareAsync(query).ConfigureAwait(false);
    }

    private static PreparedStatement Get(PreparedStatement? statement)
        => statement ?? throw new InvalidOperationException("Cassandra journal queries have not been initialized.");
}
