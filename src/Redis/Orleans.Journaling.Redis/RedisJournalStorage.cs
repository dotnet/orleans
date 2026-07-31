using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Orleans.Storage;
using StackExchange.Redis;

namespace Orleans.Journaling;

internal sealed class RedisJournalStorage : IJournalStorage
{
    private static readonly RedisValue[] NoValues = [];
    private static readonly IReadOnlyDictionary<string, string> EmptyProperties
        = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    internal const string AppendLengthMetadataKey = "$append-length";
    internal const string ContentETagMetadataKey = "$content-etag";
    internal const string ETagMetadataKey = "$etag";
    internal const string FormatMetadataKey = "$format";
    internal const string JournalIdMetadataKey = "$journal-id";

    private const int MissingStatus = 0;
    private const int SuccessStatus = 1;
    private const int AppearedStatus = 2;
    private const int ConflictStatus = -1;
    private const int CollisionStatus = -2;
    private const int InvalidMetadataStatus = -3;

    private const string CreateIfNotExistsScript =
        """
        if redis.call('EXISTS', KEYS[2]) == 1 then
            local state = redis.call(
                'HMGET',
                KEYS[2],
                '$journal-id',
                '$etag',
                '$content-etag',
                '$append-length',
                '$format')
            local appendLength = tonumber(state[4])
            if state[1] == false
                or state[2] == false
                or state[2] == ''
                or state[3] == false
                or state[3] == ''
                or appendLength == nil
                or appendLength < 0
                or appendLength % 1 ~= 0
                or tostring(appendLength) ~= state[4]
                or state[5] == false
                or state[5] == '' then
                return { -3 }
            end
            if state[1] ~= ARGV[3] then
                return { -2 }
            end

            local result = { 0 }
            local metadata = redis.call('HGETALL', KEYS[2])
            for i = 1, #metadata do
                table.insert(result, metadata[i])
            end
            return result
        end

        redis.call('DEL', KEYS[1])
        redis.call(
            'HSET',
            KEYS[2],
            '$etag', ARGV[1],
            '$content-etag', ARGV[1],
            '$journal-id', ARGV[3],
            '$append-length', 0,
            '$format', ARGV[2])

        local count = tonumber(ARGV[4])
        local index = 5
        for i = 1, count do
            redis.call('HSET', KEYS[2], ARGV[index], ARGV[index + 1])
            index = index + 2
        end

        local result = { 1 }
        local metadata = redis.call('HGETALL', KEYS[2])
        for i = 1, #metadata do
            table.insert(result, metadata[i])
        end
        return result
        """;

    private const string GetMetadataScript =
        """
        local metadata = redis.call('HGETALL', KEYS[1])
        if #metadata == 0 then
            return { 0 }
        end

        local result = { 1 }
        for i = 1, #metadata do
            table.insert(result, metadata[i])
        end
        return result
        """;

    private const string ReadScript =
        """
        local metadata = redis.call('HGETALL', KEYS[2])
        if #metadata == 0 then
            return { 0 }
        end

        local data = redis.call('GET', KEYS[1])
        if data == false then
            data = ''
        end

        local result = { 1, data }
        for i = 1, #metadata do
            table.insert(result, metadata[i])
        end
        return result
        """;

    private const string AppendOrCreateScript =
        """
        if redis.call('EXISTS', KEYS[2]) == 0 then
            if ARGV[1] == '1' then
                return { -1 }
            end

            local appendLength = string.len(ARGV[4])
            redis.call('SET', KEYS[1], ARGV[4])
            redis.call(
                'HSET',
                KEYS[2],
                '$etag', ARGV[3],
                '$content-etag', ARGV[3],
                '$journal-id', ARGV[6],
                '$append-length', appendLength,
                '$format', ARGV[5])
            return { 1, appendLength }
        end

        local state = redis.call(
            'HMGET',
            KEYS[2],
            '$journal-id',
            '$etag',
            '$content-etag',
            '$append-length',
            '$format')
        local appendLength = tonumber(state[4])
        if state[1] == false
            or state[2] == false
            or state[2] == ''
            or state[3] == false
            or state[3] == ''
            or appendLength == nil
            or appendLength < 0
            or appendLength % 1 ~= 0
            or tostring(appendLength) ~= state[4]
            or state[5] == false
            or state[5] == '' then
            return { -3 }
        end
        if state[1] ~= ARGV[6] then
            return { -2 }
        end
        if ARGV[1] == '1' and state[3] ~= ARGV[2] then
            return { -1 }
        end

        redis.call('APPEND', KEYS[1], ARGV[4])
        appendLength = appendLength + string.len(ARGV[4])
        redis.call(
            'HSET',
            KEYS[2],
            '$etag', ARGV[3],
            '$content-etag', ARGV[3],
            '$append-length', appendLength)
        if state[5] ~= ARGV[5] then
            redis.call('HSET', KEYS[2], '$format', ARGV[5])
        end

        return { 1, appendLength }
        """;

    private const string ReplaceOrCreateScript =
        """
        if redis.call('EXISTS', KEYS[2]) == 0 then
            if ARGV[1] == '1' then
                return { -1 }
            end

            redis.call('SET', KEYS[1], ARGV[4])
            redis.call(
                'HSET',
                KEYS[2],
                '$etag', ARGV[3],
                '$content-etag', ARGV[3],
                '$journal-id', ARGV[6],
                '$append-length', 0,
                '$format', ARGV[5])
            return { 1, 0 }
        end

        local state = redis.call(
            'HMGET',
            KEYS[2],
            '$journal-id',
            '$etag',
            '$content-etag',
            '$append-length',
            '$format')
        local appendLength = tonumber(state[4])
        if state[1] == false
            or state[2] == false
            or state[2] == ''
            or state[3] == false
            or state[3] == ''
            or appendLength == nil
            or appendLength < 0
            or appendLength % 1 ~= 0
            or tostring(appendLength) ~= state[4]
            or state[5] == false
            or state[5] == '' then
            return { -3 }
        end
        if state[1] ~= ARGV[6] then
            return { -2 }
        end
        if ARGV[1] == '1' and state[3] ~= ARGV[2] then
            return { -1 }
        end

        redis.call('SET', KEYS[1], ARGV[4])
        redis.call(
            'HSET',
            KEYS[2],
            '$etag', ARGV[3],
            '$content-etag', ARGV[3],
            '$append-length', 0)
        if state[5] ~= ARGV[5] then
            redis.call('HSET', KEYS[2], '$format', ARGV[5])
        end

        return { 1, 0 }
        """;

    private const string DeleteScript =
        """
        if redis.call('EXISTS', KEYS[2]) == 0 then
            if ARGV[2] == '0' then
                redis.call('DEL', KEYS[1])
                return { 1 }
            end

            return { -1 }
        end

        if ARGV[2] == '0' then
            return { 2 }
        end

        local state = redis.call(
            'HMGET',
            KEYS[2],
            '$journal-id',
            '$etag',
            '$content-etag')
        if state[1] == false
            or state[2] == false
            or state[2] == ''
            or state[3] == false
            or state[3] == '' then
            return { -3 }
        end
        if state[1] ~= ARGV[3] then
            return { -2 }
        end
        if state[3] ~= ARGV[1] then
            return { -1 }
        end

        redis.call('DEL', KEYS[1])
        redis.call('DEL', KEYS[2])
        return { 1 }
        """;

    private const string UpdateMetadataScript =
        """
        if redis.call('EXISTS', KEYS[1]) == 0 then
            return { 0 }
        end

        local state = redis.call(
            'HMGET',
            KEYS[1],
            '$journal-id',
            '$etag',
            '$content-etag',
            '$append-length',
            '$format')
        local appendLength = tonumber(state[4])
        if state[1] == false
            or state[2] == false
            or state[2] == ''
            or state[3] == false
            or state[3] == ''
            or appendLength == nil
            or appendLength < 0
            or appendLength % 1 ~= 0
            or tostring(appendLength) ~= state[4]
            or state[5] == false
            or state[5] == '' then
            return { -3 }
        end
        if state[1] ~= ARGV[4] then
            return { -2 }
        end
        if ARGV[2] == '1' and state[2] ~= ARGV[1] then
            return { 0 }
        end

        local changed = 0
        local removeCount = tonumber(ARGV[5])
        local index = 6
        for i = 1, removeCount do
            if redis.call('HDEL', KEYS[1], ARGV[index]) == 1 then
                changed = 1
            end
            index = index + 1
        end

        local setCount = tonumber(ARGV[index])
        index = index + 1
        for i = 1, setCount do
            local key = ARGV[index]
            local value = ARGV[index + 1]
            local existing = redis.call('HGET', KEYS[1], key)
            if existing == false or existing ~= value then
                redis.call('HSET', KEYS[1], key, value)
                changed = 1
            end
            index = index + 2
        end

        if changed == 1 then
            redis.call('HSET', KEYS[1], '$etag', ARGV[3])
        end

        local result = { 1 }
        local metadata = redis.call('HGETALL', KEYS[1])
        for i = 1, #metadata do
            table.insert(result, metadata[i])
        end
        return result
        """;

    private readonly IDatabase _database;
    private readonly RedisKey _dataKey;
    private readonly RedisKey[] _journalKeys;
    private readonly RedisKey _metadataKey;
    private readonly RedisKey[] _metadataKeys;
    private readonly string _journalFormatKey;
    private readonly RedisJournalStorageOptions _options;
    private readonly JournalId _journalId;
    private string? _contentETag;
    private long _appendLength;

    public RedisJournalStorage(
        IDatabase database,
        string keyPrefix,
        string keyName,
        string journalFormatKey,
        RedisJournalStorageOptions options,
        JournalId journalId)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalFormatKey);
        ArgumentNullException.ThrowIfNull(options);
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }
        _database = database;
        _dataKey = GetDataKey(keyPrefix, keyName);
        _metadataKey = GetMetadataKey(keyPrefix, keyName);
        _journalKeys = [_dataKey, _metadataKey];
        _metadataKeys = [_metadataKey];
        _journalFormatKey = journalFormatKey;
        _options = options;
        _journalId = journalId;
    }

    public bool IsCompactionRequested => _options.CompactionThresholdBytes > 0 && _appendLength >= _options.CompactionThresholdBytes;

    public async ValueTask<bool> CreateIfNotExistsAsync(
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var callerMetadata = CopyAndValidateCallerMetadata(metadata);
        var eTag = CreateETag();

        var result = await EvaluateArrayAsync(
            CreateIfNotExistsScript,
            _journalKeys,
            BuildCreateArguments(eTag, _journalFormatKey, _journalId.Value, callerMetadata)).ConfigureAwait(false);
        var status = GetStatus(result, nameof(CreateIfNotExistsAsync));
        if (status is CollisionStatus or InvalidMetadataStatus)
        {
            ThrowForStatus(status, nameof(CreateIfNotExistsAsync), expectedETag: null);
        }
        else if (status is not MissingStatus and not SuccessStatus)
        {
            ThrowForStatus(status, nameof(CreateIfNotExistsAsync), expectedETag: null);
        }

        var state = CreateStorageState(result, startIndex: 1, nameof(CreateIfNotExistsAsync));
        SetState(state);
        return status == SuccessStatus;
    }

    public async ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await EvaluateArrayAsync(
            GetMetadataScript,
            _metadataKeys,
            NoValues).ConfigureAwait(false);
        var status = GetStatus(result, nameof(GetMetadataAsync));
        if (status == MissingStatus)
        {
            ClearState();
            return null;
        }

        ThrowForStatus(status, nameof(GetMetadataAsync), expectedETag: null);
        var state = CreateStorageState(result, startIndex: 1, nameof(GetMetadataAsync));
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
        var setValues = CopyAndValidateCallerMetadata(set);
        var removeValues = CopyRemove(remove, setValues);
        var newETag = CreateETag();

        var result = await EvaluateArrayAsync(
            UpdateMetadataScript,
            _metadataKeys,
            BuildUpdateMetadataArguments(
                expectedETag,
                newETag,
                _journalId.Value,
                removeValues,
                setValues)).ConfigureAwait(false);
        var status = GetStatus(result, nameof(UpdateMetadataAsync));
        if (status == MissingStatus)
        {
            return null;
        }

        ThrowForStatus(status, nameof(UpdateMetadataAsync), expectedETag);
        var state = CreateStorageState(result, startIndex: 1, nameof(UpdateMetadataAsync));
        SetState(state);
        return state.Metadata;
    }

    public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await EvaluateArrayAsync(
            ReadScript,
            _journalKeys,
            NoValues).ConfigureAwait(false);
        var status = GetStatus(result, nameof(ReadAsync));
        if (status == MissingStatus)
        {
            ClearState();
            consumer.Complete(metadata: null);
            return;
        }

        ThrowForStatus(status, nameof(ReadAsync), expectedETag: null);
        var state = CreateStorageState(result, startIndex: 2, nameof(ReadAsync));
        SetState(state);

        ReadOnlyMemory<byte> data = (RedisValue)result[1];
        cancellationToken.ThrowIfCancellationRequested();
        consumer.Read(GetSegments(data, _options.ReadChunkSize, cancellationToken), state.Metadata, complete: true);
    }

    public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expectedContentETag = _contentETag;
        var newETag = CreateETag();
        RedisValue payload = value.IsSingleSegment ? value.First : value.ToArray();
        var result = await EvaluateArrayAsync(
            AppendOrCreateScript,
            _journalKeys,
            [
                expectedContentETag is null ? "0" : "1",
                expectedContentETag ?? string.Empty,
                newETag,
                payload,
                _journalFormatKey,
                _journalId.Value,
            ]).ConfigureAwait(false);
        var status = GetStatus(result, nameof(AppendAsync));
        ThrowForStatus(status, nameof(AppendAsync), expectedContentETag);

        _contentETag = newETag;
        _appendLength = (long)result[1];
    }

    public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expectedContentETag = _contentETag;
        var newETag = CreateETag();
        RedisValue payload = value.IsSingleSegment ? value.First : value.ToArray();
        var result = await EvaluateArrayAsync(
            ReplaceOrCreateScript,
            _journalKeys,
            [
                expectedContentETag is null ? "0" : "1",
                expectedContentETag ?? string.Empty,
                newETag,
                payload,
                _journalFormatKey,
                _journalId.Value,
            ]).ConfigureAwait(false);
        var status = GetStatus(result, nameof(ReplaceAsync));
        ThrowForStatus(status, nameof(ReplaceAsync), expectedContentETag);

        _contentETag = newETag;
        _appendLength = (long)result[1];
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expectedExists = true;
        if (_contentETag is null)
        {
            expectedExists = await GetMetadataAsync(cancellationToken).ConfigureAwait(false) is not null;
        }

        var expectedContentETag = expectedExists ? _contentETag! : string.Empty;
        var result = await EvaluateArrayAsync(
            DeleteScript,
            _journalKeys,
            [expectedContentETag, expectedExists ? "1" : "0", _journalId.Value]).ConfigureAwait(false);
        var status = GetStatus(result, nameof(DeleteAsync));
        if (status != AppearedStatus)
        {
            ThrowForStatus(status, nameof(DeleteAsync), expectedContentETag);
        }

        ClearState();
    }

    internal static RedisKey GetMetadataKey(string keyPrefix, string keyName)
    {
        var baseKey = GetJournalBaseKey(keyPrefix, keyName);
        return $"{baseKey}:metadata";
    }

    internal static RedisValue GetMetadataKeyPattern(string keyPrefix)
        => $"{EscapeRedisPattern(keyPrefix)}:journal:*:metadata";

    private static RedisKey GetDataKey(string keyPrefix, string keyName)
    {
        var baseKey = GetJournalBaseKey(keyPrefix, keyName);
        return $"{baseKey}:data";
    }

    private static string GetJournalBaseKey(string keyPrefix, string keyName)
    {
        var hashTag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyName)));
        return $"{keyPrefix}:journal:{{{hashTag}}}";
    }

    private static string EscapeRedisPattern(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '*' or '?' or '[' or ']' or '\\')
            {
                result.Append('\\');
            }

            result.Append(character);
        }

        return result.ToString();
    }

    private ValueTask<RedisResult[]> EvaluateArrayAsync(string script, RedisKey[] keys, RedisValue[] values)
        => EvaluateArrayAsync(_database, script, keys, values);

    private static async ValueTask<RedisResult[]> EvaluateArrayAsync(
        IDatabase database,
        string script,
        RedisKey[] keys,
        RedisValue[] values)
    {
        var result = (RedisResult[]?)await database.ScriptEvaluateAsync(script, keys, values).ConfigureAwait(false);
        return result is { Length: > 0 }
            ? result
            : throw new InvalidOperationException("The Redis journal storage script returned an invalid response.");
    }

    private static int GetStatus(RedisResult[] result, string operation)
    {
        if (result.Length == 0)
        {
            throw new InvalidOperationException($"The Redis journal storage {operation} script returned an empty response.");
        }

        return (int)result[0];
    }

    private void ThrowForStatus(int status, string operation, string? expectedETag)
    {
        switch (status)
        {
            case SuccessStatus:
                return;
            case ConflictStatus:
                throw new InconsistentStateException(
                    $"Version conflict ({operation}): JournalId={_journalId} ETag={expectedETag}.");
            case CollisionStatus:
                throw new InvalidOperationException(
                    $"Redis journal key mapping collision ({operation}): the configured key for JournalId={_journalId} is already owned by another journal.");
            case InvalidMetadataStatus:
                throw new InvalidOperationException(
                    $"Redis journal '{_journalId}' has missing or invalid provider metadata.");
            default:
                throw new InvalidOperationException(
                    $"The Redis journal storage {operation} script returned unexpected status {status}.");
        }
    }

    private StorageState CreateStorageState(RedisResult[] result, int startIndex, string operation)
    {
        if (result.Length <= startIndex || (result.Length - startIndex) % 2 != 0)
        {
            throw new InvalidOperationException($"Redis journal '{_journalId}' metadata is missing or malformed.");
        }

        Dictionary<string, string>? properties = null;
        string? appendLengthValue = null;
        string? contentETag = null;
        string? eTag = null;
        string? format = null;
        string? journalId = null;
        for (var i = startIndex; i < result.Length; i += 2)
        {
            var key = ((RedisValue)result[i]).ToString();
            var value = (RedisValue)result[i + 1];
            switch (key)
            {
                case ETagMetadataKey:
                    eTag = value.ToString();
                    break;
                case ContentETagMetadataKey:
                    contentETag = value.ToString();
                    break;
                case FormatMetadataKey:
                    format = value.ToString();
                    break;
                case JournalIdMetadataKey:
                    journalId = value.ToString();
                    break;
                case AppendLengthMetadataKey:
                    appendLengthValue = value.ToString();
                    break;
                default:
                    if (!IsProviderMetadataKey(key))
                    {
                        properties ??= new(
                            Math.Max(1, (result.Length - startIndex) / 2 - 5),
                            StringComparer.Ordinal);
                        properties[key] = value.ToString();
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(eTag)
            || string.IsNullOrWhiteSpace(contentETag)
            || string.IsNullOrWhiteSpace(format)
            || string.IsNullOrWhiteSpace(journalId)
            || !long.TryParse(appendLengthValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appendLength)
            || appendLength < 0)
        {
            throw new InvalidOperationException($"Redis journal '{_journalId}' has missing or invalid provider metadata.");
        }

        if (!string.Equals(journalId, _journalId.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Redis journal key mapping collision ({operation}): the configured key for JournalId={_journalId} is already owned by journal '{journalId}'.");
        }

        return new(new RedisJournalMetadata(format, eTag, properties ?? EmptyProperties), contentETag, appendLength);
    }

    private void SetState(StorageState state)
    {
        _contentETag = state.ContentETag;
        _appendLength = state.AppendLength;
    }

    private void ClearState()
    {
        _contentETag = null;
        _appendLength = 0;
    }

    private static IEnumerable<ReadOnlyMemory<byte>> GetSegments(
        ReadOnlyMemory<byte> data,
        int segmentSize,
        CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < data.Length; offset += segmentSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return data.Slice(offset, Math.Min(segmentSize, data.Length - offset));
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static RedisValue[] BuildCreateArguments(
        string eTag,
        string journalFormatKey,
        string journalId,
        IReadOnlyDictionary<string, string> metadata)
    {
        var result = new RedisValue[4 + metadata.Count * 2];
        result[0] = eTag;
        result[1] = journalFormatKey;
        result[2] = journalId;
        result[3] = metadata.Count.ToString(CultureInfo.InvariantCulture);
        var index = 4;
        foreach (var (key, value) in metadata)
        {
            result[index++] = key;
            result[index++] = value;
        }

        return result;
    }

    private static RedisValue[] BuildUpdateMetadataArguments(
        string? expectedETag,
        string newETag,
        string journalId,
        IReadOnlySet<string> remove,
        IReadOnlyDictionary<string, string> set)
    {
        var result = new RedisValue[6 + remove.Count + set.Count * 2];
        result[0] = expectedETag ?? string.Empty;
        result[1] = expectedETag is null ? "0" : "1";
        result[2] = newETag;
        result[3] = journalId;
        result[4] = remove.Count.ToString(CultureInfo.InvariantCulture);
        var index = 5;
        foreach (var key in remove)
        {
            result[index++] = key;
        }

        result[index++] = set.Count.ToString(CultureInfo.InvariantCulture);
        foreach (var (key, value) in set)
        {
            result[index++] = key;
            result[index++] = value;
        }

        return result;
    }

    private static Dictionary<string, string> CopyAndValidateCallerMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata is null)
        {
            return result;
        }

        foreach (var (key, value) in metadata)
        {
            ValidateCallerMetadataProperty(key, value);
            result.Add(key, value);
        }

        return result;
    }

    private static IReadOnlySet<string> CopyRemove(IEnumerable<string>? remove, IReadOnlyDictionary<string, string> set)
    {
        if (remove is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in remove)
        {
            ValidateCallerMetadataPropertyName(key);
            if (set.ContainsKey(key))
            {
                throw new ArgumentException($"Journal metadata property '{key}' cannot be both set and removed.", nameof(remove));
            }

            result.Add(key);
        }

        return result;
    }

    private static void ValidateCallerMetadataProperty(string key, string value)
    {
        ValidateCallerMetadataPropertyName(key);
        ArgumentNullException.ThrowIfNull(value);
    }

    private static void ValidateCallerMetadataPropertyName(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Journal metadata property names must not contain null characters.", nameof(key));
        }

        if (IsProviderMetadataKey(key))
        {
            throw new ArgumentException($"Journal metadata property '{key}' is provider-owned.", nameof(key));
        }
    }

    private static bool IsProviderMetadataKey(string key) => key.StartsWith("$", StringComparison.Ordinal);

    internal static bool TryParseJournalId(string value, out JournalId journalId)
    {
        try
        {
            journalId = new JournalId(value);
            return true;
        }
        catch (ArgumentException)
        {
            journalId = default;
            return false;
        }
    }

    private static string CreateETag() => Guid.NewGuid().ToString("N");

    private readonly record struct StorageState(
        IJournalMetadata Metadata,
        string ContentETag,
        long AppendLength);

    private sealed class RedisJournalMetadata(
        string format,
        string eTag,
        IReadOnlyDictionary<string, string> properties) : IJournalMetadata
    {
        public string Format { get; } = format;

        public string ETag { get; } = eTag;

        public IReadOnlyDictionary<string, string> Properties { get; } = properties;
    }
}
