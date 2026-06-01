using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Orleans.Storage;
using StackExchange.Redis;

namespace Orleans.Journaling;

internal sealed class RedisJournalStorage : IJournalStorage
{
    internal const string ETagMetadataKey = "$etag";
    internal const string FormatMetadataKey = "format";

    private const string CreateIfNotExistsScript =
        """
        if redis.call('EXISTS', KEYS[2]) == 1 then
            return 0
        end

        redis.call('DEL', KEYS[1])
        redis.call('HSET', KEYS[2], '$etag', ARGV[1])
        if ARGV[2] ~= '' then
            redis.call('HSET', KEYS[2], 'format', ARGV[2])
        end

        local count = tonumber(ARGV[3])
        local index = 4
        for i = 1, count do
            redis.call('HSET', KEYS[2], ARGV[index], ARGV[index + 1])
            index = index + 2
        end

        return 1
        """;

    private const string AppendScript =
        """
        local current = redis.call('HGET', KEYS[2], '$etag')
        if current == false or current ~= ARGV[1] then
            return -1
        end

        redis.call('APPEND', KEYS[1], ARGV[3])
        redis.call('HSET', KEYS[2], '$etag', ARGV[2])
        if ARGV[4] ~= '' then
            redis.call('HSET', KEYS[2], 'format', ARGV[4])
        end

        return redis.call('STRLEN', KEYS[1])
        """;

    private const string ReplaceScript =
        """
        local current = redis.call('HGET', KEYS[2], '$etag')
        if current == false or current ~= ARGV[1] then
            return -1
        end

        redis.call('SET', KEYS[1], ARGV[3])
        redis.call('HSET', KEYS[2], '$etag', ARGV[2])
        if ARGV[4] ~= '' then
            redis.call('HSET', KEYS[2], 'format', ARGV[4])
        end

        return redis.call('STRLEN', KEYS[1])
        """;

    private const string DeleteScript =
        """
        local current = redis.call('HGET', KEYS[2], '$etag')
        if current == false then
            if ARGV[1] == '' then
                redis.call('DEL', KEYS[1])
                return 1
            end

            return 0
        end

        if ARGV[1] ~= '' and current ~= ARGV[1] then
            return 0
        end

        redis.call('DEL', KEYS[1])
        redis.call('DEL', KEYS[2])
        return 1
        """;

    private const string UpdateMetadataScript =
        """
        if redis.call('EXISTS', KEYS[1]) == 0 then
            return { 0 }
        end

        local current = redis.call('HGET', KEYS[1], '$etag')
        if ARGV[2] == '1' and current ~= ARGV[1] then
            return { 0 }
        end

        local changed = 0
        local removeCount = tonumber(ARGV[4])
        local index = 5
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

        local result = redis.call('HGETALL', KEYS[1])
        table.insert(result, 1, 1)
        return result
        """;

    private readonly IDatabase _database;
    private readonly RedisKey _catalogKey;
    private readonly RedisKey _dataKey;
    private readonly RedisKey _metadataKey;
    private readonly string _journalFormatKey;
    private readonly RedisJournalStorageOptions _options;
    private readonly JournalId _journalId;
    private string? _eTag;
    private long _length;

    public RedisJournalStorage(
        IDatabase database,
        RedisKey catalogKey,
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
        _catalogKey = catalogKey;
        _dataKey = GetDataKey(keyPrefix, keyName);
        _metadataKey = GetMetadataKey(keyPrefix, keyName);
        _journalFormatKey = journalFormatKey;
        _options = options;
        _journalId = journalId;
    }

    public bool IsCompactionRequested => _options.CompactionThresholdBytes > 0 && _length >= _options.CompactionThresholdBytes;

    public async ValueTask<bool> CreateIfNotExistsAsync(
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var callerMetadata = CopyAndValidateCallerMetadata(metadata);
        await _database.SetAddAsync(_catalogKey, _journalId.Value).ConfigureAwait(false);

        var eTag = CreateETag();
        var result = (int)await _database.ScriptEvaluateAsync(
            CreateIfNotExistsScript,
            [_dataKey, _metadataKey],
            BuildCreateArguments(eTag, _journalFormatKey, callerMetadata)).ConfigureAwait(false);

        if (result == 1)
        {
            _eTag = eTag;
            _length = 0;
            return true;
        }

        _ = await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    public async ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await _database.HashGetAllAsync(_metadataKey).ConfigureAwait(false);
        var metadata = CreateJournalMetadata(entries);
        _eTag = metadata?.ETag;
        return metadata;
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

        var result = (RedisResult[]?)await _database.ScriptEvaluateAsync(
            UpdateMetadataScript,
            [_metadataKey],
            BuildUpdateMetadataArguments(expectedETag, newETag, removeValues, setValues)).ConfigureAwait(false);

        if (result is null || result.Length == 0 || (int)result[0] == 0)
        {
            return null;
        }

        var metadata = CreateJournalMetadata(result, startIndex: 1);
        _eTag = metadata?.ETag;
        return metadata;
    }

    public async ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        cancellationToken.ThrowIfCancellationRequested();

        var metadata = await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            _length = 0;
            consumer.Complete(metadata: null);
            return;
        }

        var length = await _database.StringLengthAsync(_dataKey).ConfigureAwait(false);
        _length = length;
        if (length == 0)
        {
            consumer.Complete(metadata);
            return;
        }

        var chunkSize = _options.ReadChunkSize;
        for (var offset = 0L; offset < length; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(offset + chunkSize, length) - 1;
            var chunk = await _database.StringGetRangeAsync(_dataKey, offset, end).ConfigureAwait(false);
            if (chunk.IsNull)
            {
                break;
            }

            ReadOnlyMemory<byte> bytes = chunk;
            consumer.Read(bytes, metadata, complete: false);
        }

        consumer.Complete(metadata);
    }

    public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureMetadataExistsAsync(cancellationToken).ConfigureAwait(false);
        await _database.SetAddAsync(_catalogKey, _journalId.Value).ConfigureAwait(false);

        var expectedETag = _eTag!;
        var newETag = CreateETag();
        var payload = value.ToArray();
        var length = (long)await _database.ScriptEvaluateAsync(
            AppendScript,
            [_dataKey, _metadataKey],
            [expectedETag, newETag, payload, _journalFormatKey]).ConfigureAwait(false);

        if (length < 0)
        {
            throw CreateInconsistentStateException(nameof(AppendAsync), expectedETag);
        }

        _eTag = newETag;
        _length = length;
    }

    public async ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureMetadataExistsAsync(cancellationToken).ConfigureAwait(false);
        await _database.SetAddAsync(_catalogKey, _journalId.Value).ConfigureAwait(false);

        var expectedETag = _eTag!;
        var newETag = CreateETag();
        var payload = value.ToArray();
        var length = (long)await _database.ScriptEvaluateAsync(
            ReplaceScript,
            [_dataKey, _metadataKey],
            [expectedETag, newETag, payload, _journalFormatKey]).ConfigureAwait(false);

        if (length < 0)
        {
            throw CreateInconsistentStateException(nameof(ReplaceAsync), expectedETag);
        }

        _eTag = newETag;
        _length = length;
    }

    public async ValueTask DeleteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_eTag is null)
        {
            var metadata = await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
            if (metadata is null)
            {
                await _database.KeyDeleteAsync(_dataKey).ConfigureAwait(false);
                await _database.SetRemoveAsync(_catalogKey, _journalId.Value).ConfigureAwait(false);
                _length = 0;
                return;
            }
        }

        var expectedETag = _eTag;
        var result = (int)await _database.ScriptEvaluateAsync(
            DeleteScript,
            [_dataKey, _metadataKey],
            [expectedETag ?? string.Empty]).ConfigureAwait(false);

        if (result != 1)
        {
            throw CreateInconsistentStateException(nameof(DeleteAsync), expectedETag);
        }

        await _database.SetRemoveAsync(_catalogKey, _journalId.Value).ConfigureAwait(false);
        _eTag = null;
        _length = 0;
    }

    internal static RedisKey GetCatalogKey(string keyPrefix) => $"{keyPrefix}:catalog";

    internal static RedisKey GetMetadataKey(string keyPrefix, string keyName)
    {
        var baseKey = GetJournalBaseKey(keyPrefix, keyName);
        return $"{baseKey}:metadata";
    }

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

    private async ValueTask EnsureMetadataExistsAsync(CancellationToken cancellationToken)
    {
        if (_eTag is not null)
        {
            return;
        }

        var metadata = await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        if (metadata is not null)
        {
            return;
        }

        if (await CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        metadata = await GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        if (metadata?.ETag is null)
        {
            throw new InvalidOperationException($"Redis journal '{_journalId}' metadata could not be loaded or created.");
        }
    }

    private InconsistentStateException CreateInconsistentStateException(string operation, string? expectedETag)
        => new($"Version conflict ({operation}): JournalId={_journalId} ETag={expectedETag}.");

    private static RedisValue[] BuildCreateArguments(string eTag, string journalFormatKey, IReadOnlyDictionary<string, string> metadata)
    {
        var result = new RedisValue[3 + metadata.Count * 2];
        result[0] = eTag;
        result[1] = journalFormatKey;
        result[2] = metadata.Count.ToString(CultureInfo.InvariantCulture);
        var index = 3;
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
        IReadOnlySet<string> remove,
        IReadOnlyDictionary<string, string> set)
    {
        var result = new RedisValue[5 + remove.Count + set.Count * 2];
        result[0] = expectedETag ?? string.Empty;
        result[1] = expectedETag is null ? "0" : "1";
        result[2] = newETag;
        result[3] = remove.Count.ToString(CultureInfo.InvariantCulture);
        var index = 4;
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

    private static IJournalMetadata? CreateJournalMetadata(HashEntry[] entries)
    {
        if (entries.Length == 0)
        {
            return null;
        }

        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        string? eTag = null;
        string? format = null;
        foreach (var entry in entries)
        {
            var key = entry.Name.ToString();
            var value = entry.Value.ToString();
            if (string.Equals(key, ETagMetadataKey, StringComparison.Ordinal))
            {
                eTag = value;
            }
            else if (string.Equals(key, FormatMetadataKey, StringComparison.OrdinalIgnoreCase))
            {
                format = value is { Length: > 0 } ? value : null;
            }
            else if (!IsProviderMetadataKey(key))
            {
                properties[key] = value;
            }
        }

        if (string.IsNullOrWhiteSpace(eTag))
        {
            throw new InvalidOperationException("Redis journal metadata is missing its provider ETag.");
        }

        return new JournalMetadata(format, eTag, properties);
    }

    private static IJournalMetadata? CreateJournalMetadata(RedisResult[] values, int startIndex)
    {
        if (values.Length <= startIndex)
        {
            return null;
        }

        var entries = new HashEntry[(values.Length - startIndex) / 2];
        var entryIndex = 0;
        for (var i = startIndex; i < values.Length; i += 2)
        {
            entries[entryIndex++] = new HashEntry((RedisValue)values[i], (RedisValue)values[i + 1]);
        }

        return CreateJournalMetadata(entries);
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

    private static bool IsProviderMetadataKey(string key)
        => string.Equals(key, ETagMetadataKey, StringComparison.Ordinal)
            || string.Equals(key, FormatMetadataKey, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("$", StringComparison.Ordinal);

    private static string CreateETag() => Guid.NewGuid().ToString("N");
}
