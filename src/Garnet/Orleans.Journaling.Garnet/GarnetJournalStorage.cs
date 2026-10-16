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
    private readonly GarnetJournalKeySet _keys;
    private string? _contentETag;

    // Whether the journal existed when this instance last observed it, or null if it has not observed it.
    private bool? _exists;

    // The absence ETag of the journal when this instance last observed it absent: its tombstone's ETag, or empty.
    private string _absenceETag = string.Empty;

    // The bucket's collection generation when this instance last observed the journal absent (D25).
    private string _absenceGeneration = "0";
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
        _keys = GarnetJournalKeySet.Create(router.MapId, router.BucketCount, GarnetJournalKeys.EncodeJournalId(journalId.Value));
    }

    internal int Bucket => _keys.Bucket;

    internal JournalId JournalId => _journalId;

    internal GarnetJournalKeySet Keys => _keys;

    internal RedisKey BucketKey => _keys.BucketKey;

    internal RedisKey MetadataKey => _keys.MetadataKey;

    internal RedisKey DataKey => _keys.DataKey;

    /// <summary>Gets the absence ETag this instance last observed: the tombstone's ETag, or empty for no record.</summary>
    internal string AbsenceETag => _absenceETag;

    /// <summary>Gets the ETag of the last deletion this instance attempted.</summary>
    internal string? LastDeletionETag { get; private set; }

    internal object[] GetProcedureKeys()
    {
        var keys = new object[GarnetJournalKeySet.Count];
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

            var args = new object[11 + properties.Count * 2];
            WriteKeys(args);
            args[6] = CreateETag();
            args[7] = _journalFormatKey;
            args[8] = _absenceETag;
            args[9] = _absenceGeneration;
            args[10] = properties.Count.ToString(CultureInfo.InvariantCulture);
            var index = 11;
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
                // The journal was deleted, or the bucket collected a tombstone (D25), after this instance observed its
                // absence: observe it again and retry.
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
        var args = new object[GarnetJournalKeySet.Count];
        WriteKeys(args);
        var result = await ExecuteAsync(GarnetJournalProtocol.GetMetadata, args, cancellationToken).ConfigureAwait(false);
        var status = GarnetJournalRouter.GetStatus(result);
        if (status == GarnetJournalProtocol.MissingStatus)
        {
            ClearState(GetAbsenceETag(result), GetAbsenceGeneration(result));
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

            var args = new object[11 + removeValues.Count + setValues.Count * 2];
            WriteKeys(args);
            args[6] = "1";
            args[7] = condition;
            args[8] = CreateETag();
            args[9] = removeValues.Count.ToString(CultureInfo.InvariantCulture);
            var index = 10;
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
        var args = new object[GarnetJournalKeySet.Count];
        WriteKeys(args);
        var result = await ExecuteAsync(GarnetJournalProtocol.Read, args, cancellationToken).ConfigureAwait(false);
        var status = GarnetJournalRouter.GetStatus(result);
        if (status == GarnetJournalProtocol.MissingStatus)
        {
            ClearState(GetAbsenceETag(result), GetAbsenceGeneration(result));
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
        var args = new object[10];
        var newETag = CreateETag();
        LastDeletionETag = newETag;
        WriteKeys(args);
        args[6] = expectedContentETag;
        args[7] = expectedExists ? "1" : "0";
        args[8] = newETag;

        // The tombstone records when the journal was deleted, which decides how long it is retained (D25).
        args[9] = Now();
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
    /// a retry either finds its own ETag among the journal's recent mutations or fails its condition. A write
    /// conditioned on the empty absence also names the collection generation it observed, and fails if the bucket has
    /// since collected a tombstone (D25); if no attempt can have committed and the journal is still in the empty
    /// absence, the instance observes it again and writes with the new generation.
    /// </remarks>
    private async ValueTask WriteAsync(string procedure, ReadOnlySequence<byte> value, string operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_exists is null)
        {
            await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        }

        // Copy the payload: a cancelled or timed-out call can leave the command queued in the client after this
        // method returns, and the caller may reuse its buffer.
        RedisValue payload = value.ToArray();
        while (true)
        {
            var expectedContentETag = _exists == true ? _contentETag : null;
            var newETag = CreateETag();
            var args = new object[12];
            WriteKeys(args);
            args[6] = expectedContentETag is null ? "0" : "1";
            args[7] = expectedContentETag ?? _absenceETag;
            args[8] = newETag;
            args[9] = payload;
            args[10] = _journalFormatKey;
            args[11] = _absenceGeneration;
            var ambiguous = new StrongBox<bool>();
            var result = await ExecuteAsync(procedure, args, cancellationToken, ambiguous).ConfigureAwait(false);
            var status = GarnetJournalRouter.GetStatus(result);
            if (status == GarnetJournalProtocol.ConflictStatus && !ambiguous.Value && expectedContentETag is null && _absenceETag.Length == 0)
            {
                await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
                if (_exists == false && _absenceETag.Length == 0)
                {
                    continue;
                }
            }

            ThrowForStatus(status, operation, expectedContentETag, ambiguous.Value);
            if (result.Length < 2 || !long.TryParse(result[1].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var appendLength))
            {
                throw new InvalidOperationException($"Garnet journal '{_journalId}' returned a malformed {operation} response.");
            }

            _exists = true;
            _contentETag = newETag;
            _appendLength = appendLength;
            return;
        }
    }

    private ValueTask<RedisResult[]> ExecuteAsync(string procedure, object[] args, CancellationToken cancellationToken, StrongBox<bool>? ambiguous = null)
        => _router.ExecuteJournalAsync(_keys, procedure, args, cancellationToken, ambiguous);

    private void WriteKeys(object[] args) => _keys.WriteTo(args);

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

    private static string GetAbsenceGeneration(RedisResult[] result) => result.Length > 2 ? result[2].ToString() : "0";

    private void ClearState(string absenceETag, string absenceGeneration = "0")
    {
        _absenceETag = absenceETag;
        _absenceGeneration = absenceGeneration;
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

    /// <summary>Gets the current time by this silo's clock, which a deletion records to bound its tombstone's retention.</summary>
    private string Now() => Math.Max(0, _router.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds()).ToString(CultureInfo.InvariantCulture);

    private readonly record struct StorageState(IJournalMetadata Metadata, string ContentETag, long AppendLength);
}
