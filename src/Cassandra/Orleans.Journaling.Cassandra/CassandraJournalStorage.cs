using System.Buffers;
using System.Runtime.CompilerServices;
using Cassandra;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Serialization.Buffers;
using Orleans.Storage;

namespace Orleans.Journaling;

internal sealed class CassandraJournalStorage : IJournalStorage
{
    private const int ReplaceBatchSize = 32;
    private readonly CassandraJournalStorageQueries _queries;
    private readonly CassandraJournalStorageOptions _options;
    private readonly JournalId _journalId;
    private readonly string _journalFormatKey;
    private readonly ILogger<CassandraJournalStorage> _logger;
    private HeaderState? _state;
    private bool _hasObservedState;

    public CassandraJournalStorage(
        CassandraJournalStorageQueries queries,
        CassandraJournalStorageOptions options,
        JournalId journalId,
        string journalFormatKey,
        ILogger<CassandraJournalStorage> logger)
    {
        _queries = queries;
        _options = options;
        _journalId = journalId;
        _journalFormatKey = journalFormatKey;
        _logger = logger;
    }

    public bool IsCompactionRequested
        => _state is { } state
            && (state.AppendCount >= _options.CompactionAppendCount
                || state.AppendLength >= _options.CompactionThresholdBytes);

    public async ValueTask<bool> CreateIfNotExistsAsync(
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = CassandraJournalGenerationId.Create();
        var revision = Guid.NewGuid();
        var result = await _queries.Session.ExecuteAsync(
            _queries.CreateHeader(_journalId.Value, generation, _journalFormatKey, CopyMetadata(metadata), revision, revision))
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (CassandraJournalStorageQueries.WasApplied(result))
        {
            SetState(new HeaderState(generation, 0, 0, 0, 0, _journalFormatKey, CopyMetadata(metadata), revision, revision));
            return true;
        }

        SetState(await ReadHeaderAsync(cancellationToken).ConfigureAwait(false));
        if (_state is null)
        {
            throw CreateInconsistentStateException("The Cassandra journal header disappeared during creation.", revision, null);
        }

        return false;
    }

    public async ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        SetState(await ReadHeaderAsync(cancellationToken).ConfigureAwait(false));
        return _state is { } state ? CreateMetadata(state, includeETag: true) : null;
    }

    public async ValueTask<IJournalMetadata?> UpdateMetadataAsync(
        IReadOnlyDictionary<string, string>? set = null,
        IEnumerable<string>? remove = null,
        string? expectedETag = null,
        CancellationToken cancellationToken = default)
    {
        var setValues = CopyMetadata(set);
        var removeValues = CopyRemove(remove, setValues);
        var expectedRevision = ParseETag(expectedETag);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var state = await ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                SetState(null);
                return null;
            }

            var current = state.Value;
            if (expectedRevision is { } required && current.Revision != required)
            {
                SetState(current);
                return null;
            }

            var updatedMetadata = new Dictionary<string, string>(current.Metadata, StringComparer.Ordinal);
            foreach (var key in removeValues)
            {
                updatedMetadata.Remove(key);
            }

            foreach (var (key, value) in setValues)
            {
                updatedMetadata[key] = value;
            }

            if (DictionaryEqual(current.Metadata, updatedMetadata))
            {
                SetState(current);
                return CreateMetadata(current, includeETag: true);
            }

            var revision = Guid.NewGuid();
            var result = await _queries.Session.ExecuteAsync(
                _queries.UpdateMetadata(_journalId.Value, updatedMetadata, revision, current.Revision))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (CassandraJournalStorageQueries.WasApplied(result))
            {
                current = current with { Metadata = updatedMetadata, Revision = revision };
                SetState(current);
                return CreateMetadata(current, includeETag: true);
            }

            if (expectedRevision is not null)
            {
                SetState(await ReadHeaderAsync(cancellationToken).ConfigureAwait(false));
                return null;
            }
        }

        SetState(await ReadHeaderAsync(cancellationToken).ConfigureAwait(false));
        throw CreateInconsistentStateException(
            "Cassandra journal metadata changed repeatedly while updating metadata; retry the operation.",
            expectedRevision,
            _state?.Revision);
    }

    public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        var state = await ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
        SetState(state);
        if (state is null)
        {
            using var empty = new ArcBufferWriter();
            consumer.Read(new JournalBufferReader(empty.Reader, isCompleted: true), metadata: null);
            return;
        }

        var metadata = CreateMetadata(state.Value, includeETag: false);
        using var buffer = new ArcBufferWriter();
        var rows = await _queries.Session.ExecuteAsync(
            _queries.ReadEntries(_journalId.Value, state.Value.Generation, state.Value.NextSequence))
            .ConfigureAwait(false);
        long expectedSequence = 0;
        long totalBytes = 0;
        await foreach (var row in ReadRowsAsync(rows, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sequence = row.GetValue<long>("sequence");
            if (sequence != expectedSequence)
            {
                throw CreateInconsistentStateException(
                    $"Cassandra journal entry sequence is invalid: expected {expectedSequence}, found {sequence}; recovery is required.",
                    state.Value.Revision,
                    null);
            }

            var payload = row.GetValue<byte[]>("payload");
            if (payload is null || payload.Length == 0)
            {
                throw CreateInconsistentStateException(
                    $"Cassandra journal entry {sequence} is empty; recovery is required.",
                    state.Value.Revision,
                    null);
            }

            buffer.Write(payload);
            totalBytes = checked(totalBytes + payload.Length);
            expectedSequence++;
            var reader = new JournalBufferReader(buffer.Reader, isCompleted: false);
            consumer.Read(reader, metadata);
        }

        if (expectedSequence != state.Value.NextSequence || totalBytes != state.Value.TotalLength)
        {
            throw CreateInconsistentStateException(
                $"Cassandra journal manifest expected {state.Value.NextSequence} rows and {state.Value.TotalLength} bytes, " +
                $"but recovery read {expectedSequence} rows and {totalBytes} bytes; recovery is required.",
                state.Value.Revision,
                null);
        }

        consumer.Read(new JournalBufferReader(buffer.Reader, isCompleted: true), metadata);
        if (buffer.Length != 0)
        {
            throw new InvalidOperationException("The journal storage consumer did not read all supplied journal data.");
        }
    }

    public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.IsEmpty)
        {
            return;
        }

        if (value.Length > _options.MaxAppendBytes)
        {
            throw new InvalidOperationException(
                $"Cassandra journal append of {value.Length:N0} bytes exceeds the configured maximum of {_options.MaxAppendBytes:N0} bytes.");
        }

        var payload = value.ToArray();
        var state = await EnsureHeaderAsync(cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var sequence = state.NextSequence;
            var revision = Guid.NewGuid();
            var contentRevision = Guid.NewGuid();
            var result = await _queries.Session.ExecuteAsync(
                _queries.Append(
                    _journalId.Value,
                    state.Generation,
                    sequence,
                    Guid.NewGuid(),
                    payload,
                    checked(sequence + 1),
                    checked(state.TotalLength + payload.Length),
                    checked(state.AppendCount + 1),
                    checked(state.AppendLength + payload.Length),
                    revision,
                    contentRevision,
                    state.Revision,
                    sequence))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (CassandraJournalStorageQueries.WasApplied(result))
            {
                SetState(state with
                {
                    NextSequence = checked(sequence + 1),
                    TotalLength = checked(state.TotalLength + payload.Length),
                    AppendCount = checked(state.AppendCount + 1),
                    AppendLength = checked(state.AppendLength + payload.Length),
                    Revision = revision,
                    ContentRevision = contentRevision,
                });
                return;
            }

            var current = await ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
            if (current is null || current.Value.ContentRevision != state.ContentRevision)
            {
                SetState(current);
                throw CreateInconsistentStateException("Cassandra journal changed while appending; recovery is required.", state.Revision, current?.Revision);
            }

            state = current.Value;
        }

        SetState(await ReadHeaderAsync(cancellationToken).ConfigureAwait(false));
        throw CreateInconsistentStateException("Cassandra journal metadata changed repeatedly while appending; recovery is required.", state.Revision, _state?.Revision);
    }

    public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = await EnsureHeaderAsync(cancellationToken).ConfigureAwait(false);
        var generation = CassandraJournalGenerationId.Create();
        var sequence = 0L;
        var stagedBatch = CreateReplaceBatch();
        var stagedCount = 0;
        var publicationStarted = false;
        var remaining = value;

        try
        {
            while (!remaining.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = (int)Math.Min(_options.RowSizeBytes, remaining.Length);
                var payload = remaining.Slice(0, length).ToArray();
                remaining = remaining.Slice(length);
                stagedBatch.Add(_queries.ReplaceEntry(_journalId.Value, generation, sequence, Guid.NewGuid(), payload));
                stagedCount++;
                sequence++;
                if (stagedCount >= ReplaceBatchSize)
                {
                    await _queries.Session.ExecuteAsync(stagedBatch).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    stagedBatch = CreateReplaceBatch();
                    stagedCount = 0;
                }
            }

            if (stagedCount > 0)
            {
                await _queries.Session.ExecuteAsync(stagedBatch).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var attempt = 0; attempt < 5; attempt++)
            {
                var revision = Guid.NewGuid();
                var contentRevision = Guid.NewGuid();
                publicationStarted = true;
                var result = await _queries.Session.ExecuteAsync(
                    _queries.PublishReplacement(
                        _journalId.Value,
                        generation,
                        sequence,
                        value.Length,
                        _journalFormatKey,
                        revision,
                        contentRevision,
                        state.Revision,
                        state.Generation,
                        state.NextSequence))
                    .ConfigureAwait(false);

                if (CassandraJournalStorageQueries.WasApplied(result))
                {
                    var oldGeneration = state.Generation;
                    SetState(state with
                    {
                        Generation = generation,
                        NextSequence = sequence,
                        TotalLength = value.Length,
                        AppendCount = 0,
                        AppendLength = 0,
                        Format = _journalFormatKey,
                        Revision = revision,
                        ContentRevision = contentRevision,
                    });
                    await CleanupReplacedGenerationsAsync(oldGeneration, generation).ConfigureAwait(false);
                    return;
                }

                publicationStarted = false;
                cancellationToken.ThrowIfCancellationRequested();
                var current = await ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
                if (current is null || current.Value.ContentRevision != state.ContentRevision)
                {
                    SetState(current);
                    throw CreateInconsistentStateException("Cassandra journal changed while replacing it; recovery is required.", state.Revision, current?.Revision);
                }

                state = current.Value;
            }

            SetState(await ReadHeaderAsync(cancellationToken).ConfigureAwait(false));
            throw CreateInconsistentStateException("Cassandra journal metadata changed repeatedly while replacing it; recovery is required.", state.Revision, _state?.Revision);
        }
        catch (Exception operationFailure)
        {
            if (!publicationStarted)
            {
                try
                {
                    await _queries.Session.ExecuteAsync(_queries.DeleteGeneration(_journalId.Value, generation)).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException("Cassandra journal replacement staging and cleanup both failed.", operationFailure, cleanupFailure);
                }
            }

            throw;
        }
    }

    private async ValueTask CleanupReplacedGenerationsAsync(Guid oldGeneration, Guid newGeneration)
    {
        if (oldGeneration != newGeneration)
        {
            try
            {
                await _queries.Session.ExecuteAsync(_queries.DeleteGeneration(_journalId.Value, oldGeneration))
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to delete previous Cassandra journal generation {Generation} for journal {JournalId}; replacement is already committed.",
                    oldGeneration,
                    _journalId.Value);
            }
        }

        try
        {
            await _queries.Session.ExecuteAsync(
                _queries.DeleteOlderGenerations(_journalId.Value, CassandraJournalGenerationId.CreateCleanupCutoff()))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed to delete old Cassandra journal generations for journal {JournalId}; replacement is already committed.",
                _journalId.Value);
        }
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expectedState = _state;
        var hasExpectedState = _hasObservedState;
        var state = await ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
        if (hasExpectedState
            && (expectedState is null
                ? state is not null
                : state is { } observed && observed.ContentRevision != expectedState.Value.ContentRevision))
        {
            throw CreateInconsistentStateException(
                "Cassandra journal changed while deleting it; recovery is required.",
                expectedState?.Revision,
                state?.Revision);
        }

        SetState(state);
        if (state is null)
        {
            return;
        }

        var current = state.Value;
        var result = await _queries.Session.ExecuteAsync(_queries.DeleteHeader(_journalId.Value, current.Revision))
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CassandraJournalStorageQueries.WasApplied(result))
        {
            var updated = await ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
            throw CreateInconsistentStateException("Cassandra journal changed while deleting it; recovery is required.", current.Revision, updated?.Revision);
        }

        SetState(null);
        await _queries.Session.ExecuteAsync(_queries.DeleteGeneration(_journalId.Value, current.Generation))
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async ValueTask<HeaderState> EnsureHeaderAsync(CancellationToken cancellationToken)
    {
        if (_state is { } state)
        {
            return state;
        }

        var hadObservedAbsence = _hasObservedState;
        var created = await CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!created && hadObservedAbsence)
        {
            var current = _state;
            SetState(null);
            throw CreateInconsistentStateException(
                "Cassandra journal was created after this storage handle observed it absent; recovery is required.",
                null,
                current?.Revision);
        }

        state = _state ?? throw CreateInconsistentStateException("Cassandra journal header is missing after creation.", null, null);
        return state;
    }

    private BatchStatement CreateReplaceBatch()
    {
        var batch = new BatchStatement();
        batch.SetBatchType(BatchType.Unlogged);
        batch.SetConsistencyLevel(_options.WriteConsistencyLevel);
        return batch;
    }

    private static async IAsyncEnumerable<Row> ReadRowsAsync(
        RowSet rows,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            var available = rows.GetAvailableWithoutFetching();
            using (var buffered = rows.GetEnumerator())
            {
                for (var i = 0; i < available; i++)
                {
                    if (!buffered.MoveNext())
                    {
                        break;
                    }

                    yield return buffered.Current;
                }
            }

            if (rows.IsFullyFetched)
            {
                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var fetch = rows.FetchMoreResultsAsync();
            fetch.Ignore();
            await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void SetState(HeaderState? state)
    {
        _state = state;
        _hasObservedState = true;
    }

    private async ValueTask<HeaderState?> ReadHeaderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _queries.Session.ExecuteAsync(_queries.ReadHeader(_journalId.Value)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var row = result.FirstOrDefault();
        if (row is null)
        {
            return null;
        }

        var schemaVersion = row.GetValue<int>("schema_version");
        if (schemaVersion != 1)
        {
            throw new InvalidOperationException(
                $"Cassandra journal '{_journalId}' uses unsupported schema version {schemaVersion}.");
        }

        var generation = row.GetValue<Guid>("active_generation");
        var nextSequence = row.GetValue<long>("next_sequence");
        var totalLength = row.GetValue<long>("total_length");
        var appendCount = row.GetValue<long>("append_count");
        var appendLength = row.GetValue<long>("append_length");
        var format = row.GetValue<string>("journal_format");
        var properties = row.GetValue<IDictionary<string, string>>("caller_metadata");
        var revision = row.GetValue<Guid>("revision");
        var contentRevision = row.GetValue<Guid>("content_revision");
        if (generation == Guid.Empty || nextSequence < 0 || totalLength < 0 || appendCount < 0 || appendLength < 0
            || string.IsNullOrWhiteSpace(format) || revision == Guid.Empty || contentRevision == Guid.Empty)
        {
            throw new InvalidOperationException($"Cassandra journal '{_journalId}' has invalid provider metadata.");
        }

        return new HeaderState(
            generation,
            nextSequence,
            totalLength,
            appendCount,
            appendLength,
            format,
            CopyMetadata(properties),
            revision,
            contentRevision);
    }

    private IJournalMetadata CreateMetadata(HeaderState state, bool includeETag)
        => new JournalMetadata(state.Format, includeETag ? state.Revision.ToString("D") : null, state.Metadata);

    private static Dictionary<string, string> CopyMetadata(IEnumerable<KeyValuePair<string, string>>? metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata is null)
        {
            return result;
        }

        foreach (var (key, value) in metadata)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (key.StartsWith('$') || key.IndexOf('\0', StringComparison.Ordinal) >= 0)
            {
                throw new ArgumentException($"Metadata property '{key}' is provider-owned.", nameof(metadata));
            }

            ArgumentNullException.ThrowIfNull(value);
            result.Add(key, value);
        }

        return result;
    }

    private static string[] CopyRemove(IEnumerable<string>? remove, IReadOnlyDictionary<string, string> setValues)
    {
        if (remove is null)
        {
            return [];
        }

        return remove
            .Where(key =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(key);
                if (key.StartsWith('$') || key.IndexOf('\0', StringComparison.Ordinal) >= 0)
                {
                    throw new ArgumentException($"Metadata property '{key}' is provider-owned.", nameof(remove));
                }

                return !setValues.ContainsKey(key);
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool DictionaryEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static Guid? ParseETag(string? eTag)
    {
        if (eTag is null)
        {
            return null;
        }

        if (!Guid.TryParseExact(eTag, "D", out var result))
        {
            throw new ArgumentException("The Cassandra journal ETag is invalid.", nameof(eTag));
        }

        return result;
    }

    private static InconsistentStateException CreateInconsistentStateException(string message, Guid? expectedRevision, Guid? currentRevision)
        => new(
            message,
            expectedRevision?.ToString("D") ?? "Unknown",
            currentRevision?.ToString("D") ?? "Unknown");

    private readonly record struct HeaderState(
        Guid Generation,
        long NextSequence,
        long TotalLength,
        long AppendCount,
        long AppendLength,
        string Format,
        Dictionary<string, string> Metadata,
        Guid Revision,
        Guid ContentRevision);
}
