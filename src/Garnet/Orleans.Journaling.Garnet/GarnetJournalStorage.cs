using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Orleans.Storage;
using StackExchange.Redis;

namespace Orleans.Journaling;

/// <summary>
/// Journal storage for one journal, in the partition which owns its bucket.
/// </summary>
/// <remarks>
/// The journal's data is a list with one element per append, its metadata is a hash, and the bucket's index set lists
/// the journals the bucket holds. Each operation runs as one transaction procedure (see <see cref="GarnetJournalProcedure"/>),
/// routed to the bucket's partition by <see cref="GarnetJournalRouter"/>.
/// </remarks>
internal sealed class GarnetJournalStorage : IJournalStorage
{
    private static readonly IReadOnlyDictionary<string, string> EmptyProperties
        = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    private readonly GarnetJournalRouter _router;
    private readonly JournalId _journalId;
    private readonly string _journalFormatKey;
    private readonly long _compactionThresholdBytes;
    private readonly RedisKey _bucketKey;
    private readonly RedisKey _metadataKey;
    private readonly RedisKey _dataKey;
    private readonly RedisKey _indexKey;
    private readonly RedisValue _member;
    private string? _contentETag;

    // Whether the journal existed when this instance last observed it, or null if it has not observed it.
    private bool? _exists;

    // The absence ETag of the journal when this instance last observed it absent: its tombstone's ETag, or empty.
    private string _absenceETag = string.Empty;
    private long _appendLength;

    public GarnetJournalStorage(
        GarnetJournalRouter router,
        JournalId journalId,
        string journalFormatKey,
        long compactionThresholdBytes)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalFormatKey);
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        _router = router;
        _journalId = journalId;
        _journalFormatKey = journalFormatKey;
        _compactionThresholdBytes = compactionThresholdBytes;
        var encoded = GarnetJournalKeys.EncodeJournalId(journalId.Value);
        Bucket = GarnetJournalKeys.GetBucket(encoded, router.BucketCount);
        _bucketKey = GarnetJournalKeys.GetBucketKey(router.MapId, Bucket);
        _metadataKey = GarnetJournalKeys.GetMetadataKey(Bucket, encoded);
        _dataKey = GarnetJournalKeys.GetDataKey(Bucket, encoded);
        _indexKey = GarnetJournalKeys.GetIndexKey(Bucket);
        _member = encoded;
    }

    internal int Bucket { get; }

    internal JournalId JournalId => _journalId;

    internal RedisKey BucketKey => _bucketKey;

    internal RedisKey MetadataKey => _metadataKey;

    internal RedisKey DataKey => _dataKey;

    internal object[] GetProcedureKeys()
    {
        var keys = new object[5];
        WriteKeys(keys);
        return keys;
    }

    public bool IsCompactionRequested => _compactionThresholdBytes > 0 && _appendLength >= _compactionThresholdBytes;

    public async ValueTask<bool> CreateIfNotExistsAsync(
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var properties = JournalMetadata.CopyProperties(metadata);
        while (true)
        {
            if (_exists is null)
            {
                // The create is conditioned on the absence this instance observed.
                await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            }

            var args = new object[9 + properties.Count * 2];
            WriteKeys(args);
            args[5] = CreateETag();
            args[6] = _journalFormatKey;
            args[7] = _absenceETag;
            args[8] = properties.Count.ToString(CultureInfo.InvariantCulture);
            var index = 9;
            foreach (var (key, value) in properties)
            {
                args[index++] = key;
                args[index++] = value;
            }

            var ambiguous = new StrongBox<bool>();
            var result = await ExecuteAsync(GarnetJournalProtocol.Create, args, cancellationToken, ambiguous).ConfigureAwait(false);
            var status = GarnetJournalRouter.GetStatus(result);
            if (status == GarnetJournalProtocol.ConflictStatus && !ambiguous.Value)
            {
                // The journal was deleted after this instance observed its absence: observe it again and retry.
                _exists = null;
                continue;
            }

            if (status is not GarnetJournalProtocol.SuccessStatus and not GarnetJournalProtocol.MissingStatus)
            {
                ThrowForStatus(status, nameof(CreateIfNotExistsAsync), expectedETag: null, ambiguous.Value);
            }

            if (result.Length == 1)
            {
                // A retried create committed, and the journal has since been deleted; observe it again before the next write.
                _exists = null;
            }
            else
            {
                SetState(CreateStorageState(result, startIndex: 1));
            }

            return status == GarnetJournalProtocol.SuccessStatus;
        }
    }

    public async ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var args = new object[5];
        WriteKeys(args);
        var result = await ExecuteAsync(GarnetJournalProtocol.GetMetadata, args, cancellationToken).ConfigureAwait(false);
        var status = GarnetJournalRouter.GetStatus(result);
        if (status == GarnetJournalProtocol.MissingStatus)
        {
            ClearState(GetAbsenceETag(result));
            return null;
        }

        ThrowForStatus(status, nameof(GetMetadataAsync), expectedETag: null);
        var state = CreateStorageState(result, startIndex: 1);
        SetState(state);
        return state.Metadata;
    }

    /// <remarks>
    /// Every update is conditioned on a metadata ETag, so a retry whose ETag has left the journal's history cannot apply
    /// again. An unconditional update is conditioned on the ETag this instance reads, and reads again while other
    /// writers' updates intervene. An update whose outcome is unknown and whose condition no longer holds throws
    /// <see cref="GarnetJournalAmbiguousConflictException"/>.
    /// </remarks>
    public async ValueTask<IJournalMetadata?> UpdateMetadataAsync(
        IReadOnlyDictionary<string, string>? set = null,
        IEnumerable<string>? remove = null,
        string? expectedETag = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var setValues = JournalMetadata.CopyProperties(set);
        var removeValues = CopyRemove(remove, setValues);
        var condition = expectedETag;
        while (true)
        {
            if (condition is null)
            {
                if (await GetMetadataAsync(cancellationToken).ConfigureAwait(false) is not { } current)
                {
                    return null;
                }

                condition = current.ETag ?? throw new InvalidOperationException($"Garnet journal '{_journalId}' has no metadata ETag.");
            }

            var args = new object[10 + removeValues.Count + setValues.Count * 2];
            WriteKeys(args);
            args[5] = "1";
            args[6] = condition;
            args[7] = CreateETag();
            args[8] = removeValues.Count.ToString(CultureInfo.InvariantCulture);
            var index = 9;
            foreach (var key in removeValues)
            {
                args[index++] = key;
            }

            args[index++] = setValues.Count.ToString(CultureInfo.InvariantCulture);
            foreach (var (key, value) in setValues)
            {
                args[index++] = key;
                args[index++] = value;
            }

            var ambiguous = new StrongBox<bool>();
            var result = await ExecuteAsync(GarnetJournalProtocol.UpdateMetadata, args, cancellationToken, ambiguous).ConfigureAwait(false);
            var status = GarnetJournalRouter.GetStatus(result);
            if (ambiguous.Value && status is GarnetJournalProtocol.MissingStatus or GarnetJournalProtocol.ConflictStatus)
            {
                // An earlier attempt may have committed before its ETag left the journal's history.
                ThrowForStatus(GarnetJournalProtocol.ConflictStatus, nameof(UpdateMetadataAsync), condition, ambiguous: true);
            }

            if (status == GarnetJournalProtocol.MissingStatus)
            {
                return null;
            }

            if (status == GarnetJournalProtocol.ConflictStatus)
            {
                if (expectedETag is not null)
                {
                    return null;
                }

                // Another writer updated the metadata: read it again and retry this unconditional update.
                condition = null;
                continue;
            }

            ThrowForStatus(status, nameof(UpdateMetadataAsync), condition);
            var state = CreateStorageState(result, startIndex: 1);
            SetState(state);
            return state.Metadata;
        }
    }
    public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        cancellationToken.ThrowIfCancellationRequested();
        var args = new object[5];
        WriteKeys(args);
        var result = await ExecuteAsync(GarnetJournalProtocol.Read, args, cancellationToken).ConfigureAwait(false);
        var status = GarnetJournalRouter.GetStatus(result);
        if (status == GarnetJournalProtocol.MissingStatus)
        {
            ClearState(GetAbsenceETag(result));
            consumer.Complete(metadata: null);
            return;
        }

        ThrowForStatus(status, nameof(ReadAsync), expectedETag: null);
        if (result.Length < 2 || !int.TryParse(result[1].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var metadataCount)
            || result.Length < 2 + metadataCount)
        {
            throw new InvalidOperationException($"Garnet journal '{_journalId}' returned a malformed read response.");
        }

        var state = CreateStorageState(result, startIndex: 2, endIndex: 2 + metadataCount);
        SetState(state);
        cancellationToken.ThrowIfCancellationRequested();
        consumer.Read(GetSegments(result, 2 + metadataCount, cancellationToken), state.Metadata, complete: true);
    }

    public ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        => WriteAsync(GarnetJournalProtocol.Append, value, nameof(AppendAsync), cancellationToken);

    public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
        => WriteAsync(GarnetJournalProtocol.Replace, value, nameof(ReplaceAsync), cancellationToken);

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Refresh a cached absence, so a journal created since this instance observed it absent is deleted; only a
        // creation concurrent with this call can still make it a no-op.
        if (_exists != true)
        {
            await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        }

        var expectedExists = _exists == true;
        var expectedContentETag = expectedExists ? _contentETag! : string.Empty;
        var newETag = CreateETag();
        var args = new object[8];
        WriteKeys(args);
        args[5] = expectedContentETag;
        args[6] = expectedExists ? "1" : "0";
        args[7] = newETag;
        var ambiguous = new StrongBox<bool>();
        var result = await ExecuteAsync(GarnetJournalProtocol.Delete, args, cancellationToken, ambiguous).ConfigureAwait(false);
        var status = GarnetJournalRouter.GetStatus(result);
        if (status == GarnetJournalProtocol.AppearedStatus)
        {
            // The journal exists, though this instance last observed it absent: observe it again before the next write.
            _exists = null;
            return;
        }

        ThrowForStatus(status, nameof(DeleteAsync), expectedContentETag, ambiguous.Value);
        if (expectedExists)
        {
            // The deletion's tombstone carries its ETag, which identifies this absence.
            ClearState(newETag);
        }
    }

    /// <remarks>
    /// Every write is conditional: on the content ETag this instance last observed, or on the journal's absence. An
    /// instance which has not observed the journal reads its metadata first. Conditional writes make retries safe:
    /// a retry either finds its own ETag among the journal's recent mutations or fails its condition.
    /// </remarks>
    private async ValueTask WriteAsync(string procedure, ReadOnlySequence<byte> value, string operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_exists is null)
        {
            await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        }

        var expectedContentETag = _exists == true ? _contentETag : null;
        var newETag = CreateETag();

        // Copy the payload: a cancelled or timed-out call can leave the command queued in the client after this
        // method returns, and the caller may reuse its buffer.
        RedisValue payload = value.ToArray();
        var args = new object[10];
        WriteKeys(args);
        args[5] = expectedContentETag is null ? "0" : "1";
        args[6] = expectedContentETag ?? _absenceETag;
        args[7] = newETag;
        args[8] = payload;
        args[9] = _journalFormatKey;
        var ambiguous = new StrongBox<bool>();
        var result = await ExecuteAsync(procedure, args, cancellationToken, ambiguous).ConfigureAwait(false);
        var status = GarnetJournalRouter.GetStatus(result);
        ThrowForStatus(status, operation, expectedContentETag, ambiguous.Value);
        if (result.Length < 2 || !long.TryParse(result[1].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var appendLength))
        {
            throw new InvalidOperationException($"Garnet journal '{_journalId}' returned a malformed {operation} response.");
        }

        _exists = true;
        _contentETag = newETag;
        _appendLength = appendLength;
    }

    private ValueTask<RedisResult[]> ExecuteAsync(string procedure, object[] args, CancellationToken cancellationToken, StrongBox<bool>? ambiguous = null)
        => _router.ExecuteAsync(Bucket, procedure, args, cancellationToken, ambiguous);

    private void WriteKeys(object[] args)
    {
        args[0] = _bucketKey;
        args[1] = _metadataKey;
        args[2] = _dataKey;
        args[3] = _indexKey;
        args[4] = _member;
    }

    private void ThrowForStatus(int status, string operation, string? expectedETag, bool ambiguous = false)
    {
        switch (status)
        {
            case GarnetJournalProtocol.SuccessStatus:
                return;
            case GarnetJournalProtocol.ConflictStatus when ambiguous:
                throw new GarnetJournalAmbiguousConflictException(
                    $"Version conflict ({operation}) after an attempt whose outcome is unknown, which may have committed: JournalId={_journalId} ETag={expectedETag}.");
            case GarnetJournalProtocol.ConflictStatus:
                throw new InconsistentStateException(
                    $"Version conflict ({operation}): JournalId={_journalId} ETag={expectedETag}.");
            case GarnetJournalProtocol.InvalidMetadataStatus:
                throw new InvalidOperationException($"Garnet journal '{_journalId}' has missing or invalid provider metadata.");
            case GarnetJournalProtocol.UnsupportedSchemaVersionStatus:
                throw new NotSupportedException(
                    $"Garnet journal '{_journalId}' does not use supported storage schema version '{GarnetJournalProtocol.CurrentSchemaVersion}'.");
            case GarnetJournalProtocol.NeedPullStatus:
                throw new InvalidOperationException(
                    $"Garnet journal '{_journalId}' bucket {Bucket} has an incomplete migration ({operation}), which this version cannot complete.");
            default:
                throw new InvalidOperationException($"The Garnet journal {operation} procedure returned unexpected status {status}.");
        }
    }

    private StorageState CreateStorageState(RedisResult[] result, int startIndex, int? endIndex = null)
    {
        var end = endIndex ?? result.Length;
        if (end <= startIndex || (end - startIndex) % 2 != 0)
        {
            throw new InvalidOperationException($"Garnet journal '{_journalId}' metadata is missing or malformed.");
        }

        Dictionary<string, string>? properties = null;
        string? appendLengthValue = null;
        string? contentETag = null;
        string? eTag = null;
        string? format = null;
        string? schemaVersion = null;
        for (var i = startIndex; i < end; i += 2)
        {
            var key = result[i].ToString();
            var value = result[i + 1].ToString();
            switch (key)
            {
                case GarnetJournalProtocol.ETagMetadataKey:
                    eTag = value;
                    break;
                case GarnetJournalProtocol.ContentETagMetadataKey:
                    contentETag = value;
                    break;
                case GarnetJournalProtocol.FormatMetadataKey:
                    format = value;
                    break;
                case GarnetJournalProtocol.AppendLengthMetadataKey:
                    appendLengthValue = value;
                    break;
                case GarnetJournalProtocol.SchemaVersionMetadataKey:
                    schemaVersion = value;
                    break;
                default:
                    if (!JournalMetadata.IsProviderOwned(key))
                    {
                        properties ??= new(StringComparer.Ordinal);
                        properties[key] = value;
                    }

                    break;
            }
        }

        if (!string.Equals(schemaVersion, GarnetJournalProtocol.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Garnet journal '{_journalId}' uses storage schema version '{schemaVersion ?? "<missing>"}'; "
                + $"supported version is '{GarnetJournalProtocol.CurrentSchemaVersion}'.");
        }

        if (string.IsNullOrWhiteSpace(eTag)
            || string.IsNullOrWhiteSpace(contentETag)
            || string.IsNullOrWhiteSpace(format)
            || !long.TryParse(appendLengthValue, NumberStyles.None, CultureInfo.InvariantCulture, out var appendLength))
        {
            throw new InvalidOperationException($"Garnet journal '{_journalId}' has missing or invalid provider metadata.");
        }

        return new(new JournalMetadata(format, eTag, properties ?? EmptyProperties), contentETag, appendLength);
    }

    private void SetState(StorageState state)
    {
        _exists = true;
        _contentETag = state.ContentETag;
        _appendLength = state.AppendLength;
    }

    private static string GetAbsenceETag(RedisResult[] result) => result.Length > 1 ? result[1].ToString() : string.Empty;

    private void ClearState(string absenceETag)
    {
        _absenceETag = absenceETag;
        _exists = false;
        _contentETag = null;
        _appendLength = 0;
    }

    private static IEnumerable<ReadOnlyMemory<byte>> GetSegments(RedisResult[] result, int startIndex, CancellationToken cancellationToken)
    {
        for (var i = startIndex; i < result.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return (byte[]?)result[i] ?? [];
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static IReadOnlySet<string> CopyRemove(IEnumerable<string>? remove, IReadOnlyDictionary<string, string> set)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (remove is null)
        {
            return result;
        }

        foreach (var key in remove)
        {
            JournalMetadata.ValidateCallerPropertyName(key);
            if (set.ContainsKey(key))
            {
                throw new ArgumentException($"Journal metadata property '{key}' cannot be both set and removed.", nameof(remove));
            }

            result.Add(key);
        }

        return result;
    }

    private static string CreateETag() => Guid.NewGuid().ToString("N");

    private readonly record struct StorageState(IJournalMetadata Metadata, string ContentETag, long AppendLength);
}
