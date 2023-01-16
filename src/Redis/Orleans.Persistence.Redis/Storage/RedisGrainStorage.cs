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
        private const string WriteScript =
            """
            local etag = redis.call('HGET', KEYS[1], 'etag')
            if etag == false or etag == ARGV[1] then
              local result = redis.call('HMSET', KEYS[1], 'etag', ARGV[2], 'data', ARGV[3])
              if ARGV[4] ~= '-1' then
                redis.call('EXPIRE', KEYS[1], ARGV[4])
              end
              return result
            else
              return false
            end
            """;

        private readonly string _serviceId;
        private readonly RedisValue _ttl;
        private readonly RedisKey _keyPrefix;
        private readonly string _name;
        private readonly ILogger _logger;
        private readonly RedisStorageOptions _options;
        private readonly IGrainStorageSerializer _grainStorageSerializer;

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
                         _options.DeleteOnClear);
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
            var key = GetKey(grainId);

            try
            {
                var hashEntries = await _db.HashGetAllAsync(key).ConfigureAwait(false);
                if (hashEntries.Length == 2)
                {
                    var etagEntry = hashEntries.Single(e => e.Name == "etag");
                    var valueEntry = hashEntries.Single(e => e.Name == "data");

                    grainState.State = _grainStorageSerializer.Deserialize<T>(valueEntry.Value);

                    grainState.ETag = etagEntry.Value;
                }
                else
                {
                    grainState.ETag = Guid.NewGuid().ToString();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(
                    "Failed to read grain state for {GrainType} grain with id {GrainId} and storage key {Key}.",
                    grainType,
                    grainId,
                    key);
                throw new RedisStorageException(Invariant($"Failed to read grain state for {grainType} grain with id {grainId} and storage key {key}."), e);
            }
        }

        /// <inheritdoc />
        public async Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            var etag = grainState.ETag ?? "null";
            var key = GetKey(grainId);
            var newEtag = Guid.NewGuid().ToString("N");

            RedisResult response;
            try
            {
                var payload = new RedisValue(_grainStorageSerializer.Serialize<T>(grainState.State).ToString());
                var keys = new RedisKey[] { key };
                var args = new RedisValue[] { etag, newEtag, payload, _ttl };
                response = await _db.ScriptEvaluateAsync(WriteScript, keys, args).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(
                    "Failed to write grain state for {GrainType} grain with ID: {GrainId} with redis key {Key}.",
                    grainType,
                    grainId,
                    key);
                throw new RedisStorageException(
                    Invariant($"Failed to write grain state for {grainType} grain with ID: {grainId} with redis key {key}. {e.GetType()}: {e.Message}"));
            }

            if (response is not null && response.IsNull)
            {
                throw new InconsistentStateException(Invariant($"ETag mismatch - tried with ETag: {grainState.ETag}"));
            }

            grainState.ETag = newEtag;
        }

        private RedisKey GetKey(GrainId grainId) => _keyPrefix.Append(grainId.ToString());

        /// <inheritdoc />
        public async Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            try
            {
                await _db.KeyDeleteAsync(GetKey(grainId)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new RedisStorageException(Invariant($"{exception.GetType()}: {exception.Message}"));
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