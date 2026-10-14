using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using Orleans.Storage;
using StackExchange.Redis;

namespace Orleans.Journaling;

/// <summary>
/// Journal storage for one journal in a Garnet partition.
/// </summary>
/// <remarks>
/// The journal's data is a list with one element per append, its metadata is a hash, and the bucket's index set lists
/// the journals the bucket holds. Each operation runs as one transaction procedure (see <see cref="GarnetJournalProcedure"/>).
/// </remarks>
internal sealed class GarnetJournalStorage : IJournalStorage
{
    private static readonly IReadOnlyDictionary<string, string> EmptyProperties
        = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    private readonly GarnetJournalPartitionClient _client;
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
    private long _appendLength;

    public GarnetJournalStorage(
        GarnetJournalPartitionClient client,
        JournalId journalId,
        string journalFormatKey,
        long compactionThresholdBytes)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalFormatKey);
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        _client = client;
        _journalId = journalId;
        _journalFormatKey = journalFormatKey;
        _compactionThresholdBytes = compactionThresholdBytes;
        var encoded = GarnetJournalKeys.EncodeJournalId(journalId.Value);
        Bucket = GarnetJournalKeys.GetBucket(encoded, client.BucketCount);
        _bucketKey = GarnetJournalKeys.GetBucketKey(Bucket);
        _metadataKey = GarnetJournalKeys.GetMetadataKey(Bucket, encoded);
        _dataKey = GarnetJournalKeys.GetDataKey(Bucket, encoded);
        _indexKey = GarnetJournalKeys.GetIndexKey(Bucket);
        _member = encoded;
    }

    internal int Bucket { get; }

    internal JournalId JournalId => _journalId;

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
        var args = new object[8 + properties.Count * 2];
        WriteKeys(args);
        args[5] = CreateETag();
        args[6] = _journalFormatKey;
        args[7] = properties.Count.ToString(CultureInfo.InvariantCulture);
        var index = 8;
        foreach (var (key, value) in properties)
        {
            args[index++] = key;
            args[index++] = value;
        }

        var result = await ExecuteAsync(GarnetJournalProtocol.Create, args, cancellationToken).ConfigureAwait(false);
        var status = GarnetJournalPartitionClient.GetStatus(result);
        if (status is not GarnetJournalProtocol.SuccessStatus and not GarnetJournalProtocol.MissingStatus)
        {
            ThrowForStatus(status, nameof(CreateIfNotExistsAsync), expectedETag: null);
        }

        if (result.Length == 1)
        {
            // A retried create committed, and the journal has since been deleted.
            ClearState();
        }
        else
        {
            SetState(CreateStorageState(result, startIndex: 1));
        }

        return status == GarnetJournalProtocol.SuccessStatus;
    }

    public async ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var args = new object[5];
        WriteKeys(args);
        var result = await ExecuteAsync(GarnetJournalProtocol.GetMetadata, args, cancellationToken).ConfigureAwait(false);
        var status = GarnetJournalPartitionClient.GetStatus(result);
        if (status == GarnetJournalProtocol.MissingStatus)
        {
            ClearState();
            return null;
        }

        ThrowForStatus(status, nameof(GetMetadataAsync), expectedETag: null);
        var state = CreateStorageState(result, startIndex: 1);
        SetState(state);
        return state.Metadata;
    }

    public async ValueTask<IJournalMetadata?> UpdateMetadataAsync(
        IReadOnlyDictionary<string, string>? set = null,
        IEnumerable<string>? remove = null,
        string? expectedETag = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var setValues = JournalMetadata.CopyProperties(set);
        var removeValues = CopyRemove(remove, setValues);
        var args = new object[10 + removeValues.Count + setValues.Count * 2];
        WriteKeys(args);
        args[5] = expectedETag is null ? "0" : "1";
        args[6] = expectedETag ?? string.Empty;
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

        var result = await ExecuteAsync(GarnetJournalProtocol.UpdateMetadata, args, cancellationToken).ConfigureAwait(false);
        var status = GarnetJournalPartitionClient.GetStatus(result);
        if (status == GarnetJournalProtocol.MissingStatus)
        {
            return null;
        }

        ThrowForStatus(status, nameof(UpdateMetadataAsync), expectedETag);
        var state = CreateStorageState(result, startIndex: 1);
        SetState(state);
        return state.Metadata;
    }

    public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        cancellationToken.ThrowIfCancellationRequested();
        var args = new object[5];
        WriteKeys(args);
        var result = await ExecuteAsync(GarnetJournalProtocol.Read, args, cancellationToken).ConfigureAwait(false);
        var status = GarnetJournalPartitionClient.GetStatus(result);
        if (status == GarnetJournalProtocol.MissingStatus)
        {
            ClearState();
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
        if (_exists is null)
        {
            await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        }

        var expectedExists = _exists == true;
        var expectedContentETag = expectedExists ? _contentETag! : string.Empty;
        var args = new object[8];
        WriteKeys(args);
        args[5] = expectedContentETag;
        args[6] = expectedExists ? "1" : "0";
        args[7] = CreateETag();
        var result = await ExecuteAsync(GarnetJournalProtocol.Delete, args, cancellationToken).ConfigureAwait(false);
        var status = GarnetJournalPartitionClient.GetStatus(result);
        if (status != GarnetJournalProtocol.AppearedStatus)
        {
            ThrowForStatus(status, nameof(DeleteAsync), expectedContentETag);
        }

        ClearState();
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
        args[6] = expectedContentETag ?? string.Empty;
        args[7] = newETag;
        args[8] = payload;
        args[9] = _journalFormatKey;
        var result = await ExecuteAsync(procedure, args, cancellationToken).ConfigureAwait(false);
        var status = GarnetJournalPartitionClient.GetStatus(result);
        ThrowForStatus(status, operation, expectedContentETag);
        if (result.Length < 2 || !long.TryParse(result[1].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var appendLength))
        {
            throw new InvalidOperationException($"Garnet journal '{_journalId}' returned a malformed {operation} response.");
        }

        _exists = true;
        _contentETag = newETag;
        _appendLength = appendLength;
    }

    private ValueTask<RedisResult[]> ExecuteAsync(string procedure, object[] args, CancellationToken cancellationToken)
        => _client.ExecuteAsync(procedure, args, cancellationToken);

    private void WriteKeys(object[] args)
    {
        args[0] = _bucketKey;
        args[1] = _metadataKey;
        args[2] = _dataKey;
        args[3] = _indexKey;
        args[4] = _member;
    }

    private void ThrowForStatus(int status, string operation, string? expectedETag)
    {
        switch (status)
        {
            case GarnetJournalProtocol.SuccessStatus:
                return;
            case GarnetJournalProtocol.ConflictStatus:
                throw new InconsistentStateException(
                    $"Version conflict ({operation}): JournalId={_journalId} ETag={expectedETag}.");
            case GarnetJournalProtocol.InvalidMetadataStatus:
                throw new InvalidOperationException($"Garnet journal '{_journalId}' has missing or invalid provider metadata.");
            case GarnetJournalProtocol.UnsupportedSchemaVersionStatus:
                throw new NotSupportedException(
                    $"Garnet journal '{_journalId}' does not use supported storage schema version '{GarnetJournalProtocol.CurrentSchemaVersion}'.");
            case GarnetJournalProtocol.WrongPartitionStatus:
                throw new InvalidOperationException(
                    $"Garnet journal '{_journalId}' bucket {Bucket} is not open in its partition ({operation}).");
            case GarnetJournalProtocol.NeedPullStatus:
                throw new InvalidOperationException(
                    $"Garnet journal '{_journalId}' bucket {Bucket} has an incomplete migration ({operation}).");
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

    private void ClearState()
    {
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
