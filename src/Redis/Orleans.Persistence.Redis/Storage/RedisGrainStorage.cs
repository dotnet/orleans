using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence.Redis;
using Orleans.Runtime;
using Orleans.Storage;
using StackExchange.Redis;
using static System.FormattableString;

namespace Orleans.Persistence
{
    /// <summary>
    /// Redis-based grain storage provider
    /// </summary>
    internal class RedisGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly string _serviceId;
        private readonly RedisValue _ttl;
        private readonly RedisKey _keyPrefix;
        private readonly string _name;
        private readonly ILogger _logger;
        private readonly RedisStorageOptions _options;
        private readonly IGrainStorageSerializer _grainStorageSerializer;
        private readonly Func<string, GrainId, RedisKey> _getKeyFunc;
        private IConnectionMultiplexer _connection;
        private IDatabase _db;

        /// <summary>
        /// Creates a new instance of the <see cref="RedisGrainStorage"/> type.
        /// </summary>
        public RedisGrainStorage(
            string name,
            RedisStorageOptions options,
            IGrainStorageSerializer grainStorageSerializer,
            IOptions<ClusterOptions> clusterOptions,
            ILogger<RedisGrainStorage> logger)
        {
            _name = name;
            _logger = logger;
            _options = options;
            _grainStorageSerializer = options.GrainStorageSerializer ?? grainStorageSerializer;
            _serviceId = clusterOptions.Value.ServiceId;
            _ttl = options.EntryExpiry is { } ts ? ts.TotalSeconds.ToString(CultureInfo.InvariantCulture) : "-1";
            _keyPrefix = Encoding.UTF8.GetBytes($"{_serviceId}/state/");
            _getKeyFunc = _options.GetStorageKey ?? DefaultGetStorageKey;
        }

        /// <inheritdoc />
        public void Participate(ISiloLifecycle lifecycle)
        {
            var name = OptionFormattingUtilities.Name<RedisGrainStorage>(_name);
            lifecycle.Subscribe(name, _options.InitStage, Init, Close);
        }

        private async Task Init(CancellationToken cancellationToken)
        {
            var timer = Stopwatch.StartNew();

            try
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "RedisGrainStorage {Name} is initializing: ServiceId={ServiceId} DeleteOnClear={DeleteOnClear}",
                         _name,
                         _serviceId,
                         _options.DeleteStateOnClear);
                }

                _connection = await _options.CreateMultiplexer(_options).ConfigureAwait(false);
                _db = _connection.GetDatabase();

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    timer.Stop();
                    _logger.LogDebug(
                        "Init: Name={Name} ServiceId={ServiceId}, initialized in {ElapsedMilliseconds} ms",
                        _name,
                        _serviceId,
                        timer.Elapsed.TotalMilliseconds.ToString("0.00"));
                }
            }
            catch (Exception ex)
            {
                timer.Stop();
                _logger.LogError(
                    ex,
                    "Init: Name={Name} ServiceId={ServiceId}, errored in {ElapsedMilliseconds} ms.",
                    _name,
                    _serviceId,
                    timer.Elapsed.TotalMilliseconds.ToString("0.00"));

                throw new RedisStorageException(Invariant($"{ex.GetType()}: {ex.Message}"));
            }
        }

        /// <inheritdoc />
        public async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            var key = _getKeyFunc(grainType, grainId);

            try
            {
                var hashEntries = await _db.HashGetAllAsync(key).ConfigureAwait(false);
                if (hashEntries.Length == 2)
                {
                    string eTag = hashEntries.Single(static e => e.Name == "etag").Value;
                    ReadOnlyMemory<byte> data = hashEntries.Single(static e => e.Name == "data").Value;

                    if (data.Length > 0)
                    {
                        grainState.State = _grainStorageSerializer.Deserialize<T>(data);
                    }
                    else
                    {
                        grainState.State = Activator.CreateInstance<T>();
                    }

                    grainState.ETag = eTag;
                    grainState.RecordExists = true;
                }
                else
                {
                    grainState.ETag = null;
                    grainState.RecordExists = false;
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Failed to read grain state for {GrainType} grain with ID {GrainId} and storage key {Key}.",
                    grainType,
                    grainId,
                    key);
                throw new RedisStorageException(Invariant($"Failed to read grain state for {grainType} with ID {grainId} and storage key {key}. {exception.GetType()}: {exception.Message}"));
            }
        }

        /// <inheritdoc />
        public async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            const string WriteScript =
                """
                local etag = redis.call('HGET', KEYS[1], 'etag')
                if (not etag and (ARGV[1] == nil or ARGV[1] == '')) or etag == ARGV[1] then
                  redis.call('HMSET', KEYS[1], 'etag', ARGV[2], 'data', ARGV[3])
                  if ARGV[4] ~= '-1' then
                    redis.call('EXPIRE', KEYS[1], ARGV[4])
                  end
                  return 0
                else
                  return -1
                end
                """;

            var key = _getKeyFunc(grainType, grainId);
            RedisValue etag = grainState.ETag ?? "";
            RedisValue newEtag = Guid.NewGuid().ToString("N");

            try
            {
                RedisValue payload = _grainStorageSerializer.Serialize<T>(grainState.State).ToMemory();
                var keys = new RedisKey[] { key };
                var args = new RedisValue[] { etag, newEtag, payload, _ttl };
                var response = await _db.ScriptEvaluateAsync(WriteScript, keys, args).ConfigureAwait(false);

                if (response is not null && (int)response == -1)
                {
                    throw new InconsistentStateException($"Version conflict ({nameof(WriteStateAsync)}): ServiceId={_serviceId} ProviderName={_name} GrainType={grainType} GrainId={grainId} ETag={grainState.ETag}.");
                }

                grainState.ETag = newEtag;
                grainState.RecordExists = true;
            }
            catch (Exception exception) when (exception is not InconsistentStateException)
            {
                _logger.LogError(
                    "Failed to write grain state for {GrainType} grain with ID {GrainId} and storage key {Key}.",
                    grainType,
                    grainId,
                    key);
                throw new RedisStorageException(
                    Invariant($"Failed to write grain state for {grainType} grain with ID {grainId} and storage key {key}. {exception.GetType()}: {exception.Message}"));

            }
        }

        /// <summary>
        /// Default implementation of <see cref="RedisStorageOptions.GetStorageKey"/> which returns a key equivalent to <c>{ServiceId}/state/{grainId}/{grainType}</c>
        /// </summary>
        private RedisKey DefaultGetStorageKey(string grainType, GrainId grainId)
        {
            var grainIdTypeBytes = IdSpan.UnsafeGetArray(grainId.Type.Value);
            var grainIdKeyBytes = IdSpan.UnsafeGetArray(grainId.Key);
            var grainTypeLength = Encoding.UTF8.GetByteCount(grainType);
            var suffix = new byte[grainIdTypeBytes.Length + 1 + grainIdKeyBytes.Length + 1 + grainTypeLength];
            var index = 0;

            grainIdTypeBytes.CopyTo(suffix, 0);
            index += grainIdTypeBytes.Length;

            suffix[index++] = (byte)'/';

            grainIdKeyBytes.CopyTo(suffix, index);
            index += grainIdKeyBytes.Length;

            suffix[index++] = (byte)'/';

            var bytesWritten = Encoding.UTF8.GetBytes(grainType, suffix.AsSpan(index));

            Debug.Assert(bytesWritten == grainTypeLength);
            Debug.Assert(index + bytesWritten == suffix.Length);
            return _keyPrefix.Append(suffix);
        }

        /// <inheritdoc />
        public async Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            try
            {
                RedisValue etag = grainState.ETag ?? "";
                RedisResult response;
                string newETag;
                var key = _getKeyFunc(grainType, grainId);
                if (_options.DeleteStateOnClear)
                {
                    const string DeleteScript =
                        """
                        local etag = redis.call('HGET', KEYS[1], 'etag')
                        if (not etag and not ARGV[1]) or etag == ARGV[1] then
                          redis.call('DEL', KEYS[1])
                          return 0
                        else
                          return -1
                        end
                        """;
                    response = await _db.ScriptEvaluateAsync(DeleteScript, keys: new[] { key }, values: new[] { etag }).ConfigureAwait(false);
                    newETag = null;
                }
                else
                {
                    const string ClearScript =
                        """
                        local etag = redis.call('HGET', KEYS[1], 'etag')
                        if (not etag and not ARGV[1]) or etag == ARGV[1] then
                          redis.call('HMSET', KEYS[1], 'etag', ARGV[2], 'data', '')
                          return 0
                        else
                          return -1
                        end
                        """;
                    newETag = Guid.NewGuid().ToString("N");
                    response = await _db.ScriptEvaluateAsync(ClearScript, keys: new[] { key }, values: new RedisValue[] { etag, newETag }).ConfigureAwait(false);
                }

                if (response is not null && (int)response == -1)
                {
                    throw new InconsistentStateException($"Version conflict ({nameof(ClearStateAsync)}): ServiceId={_serviceId} ProviderName={_name} GrainType={grainType} GrainId={grainId} ETag={grainState.ETag}.");
                }

                grainState.ETag = newETag;
                grainState.RecordExists = false;
            }
            catch (Exception exception) when (exception is not InconsistentStateException)
            {
                throw new RedisStorageException(Invariant($"Failed to clear grain state for grain {grainType} with ID {grainId}. {exception.GetType()}: {exception.Message}"));
            }
        }

        private async Task Close(CancellationToken cancellationToken)
        {
            if (_connection is null) return;

            await _connection.CloseAsync().ConfigureAwait(false);
            _connection.Dispose();
        }
    }
}