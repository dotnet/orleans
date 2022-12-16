using System;
using System.Diagnostics;
using System.Linq;
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
    public class RedisGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private const string WriteScript = "local etag = redis.call('HGET', KEYS[1], 'etag')\nif etag == false or etag == ARGV[1] then return redis.call('HMSET', KEYS[1], 'etag', ARGV[2], 'data', ARGV[3]) else return false end";
        private const int ReloadWriteScriptMaxCount = 3;

        private readonly string _serviceId;
        private readonly string _name;
        private readonly ILogger _logger;
        private readonly RedisStorageOptions _options;
        private readonly IGrainStorageSerializer _grainStorageSerializer;

        private ConnectionMultiplexer _connection;
        private IDatabase _db;
        private ConfigurationOptions _redisOptions;
        private LuaScript _preparedWriteScript;
        private byte[] _preparedWriteScriptHash;

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
            _grainStorageSerializer = grainStorageSerializer;

            _serviceId = clusterOptions.Value.ServiceId;
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
                        "RedisGrainStorage {Name} is initializing: ServiceId={ServiceId} DatabaseNumber={DatabaseNumber} DeleteOnClear={DeleteOnClear}",
                         _name,
                         _serviceId,
                         _options.DatabaseNumber,
                         _options.DeleteOnClear);
                }

                _redisOptions = ConfigurationOptions.Parse(_options.ConnectionString);
                _connection = await ConnectionMultiplexer.ConnectAsync(_redisOptions).ConfigureAwait(false);

                if (_options.DatabaseNumber.HasValue)
                {
                    _db = _connection.GetDatabase(_options.DatabaseNumber.Value);
                }
                else
                {
                    _db = _connection.GetDatabase();
                }

                _preparedWriteScript = LuaScript.Prepare(WriteScript);
                _preparedWriteScriptHash = await LoadWriteScriptAsync().ConfigureAwait(false);

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
                throw;
            }
        }

        private async Task<byte[]> LoadWriteScriptAsync()
        {
            Debug.Assert(_connection is not null);
            Debug.Assert(_preparedWriteScript is not null);
            Debug.Assert(_redisOptions.EndPoints.Count > 0);

            System.Net.EndPoint[] endPoints = _connection.GetEndPoints();
            var loadTasks = new Task<LoadedLuaScript>[endPoints.Length];
            for (int i = 0; i < endPoints.Length; i++)
            {
                var endpoint = endPoints.ElementAt(i);
                var server = _connection.GetServer(endpoint);

                loadTasks[i] = _preparedWriteScript.LoadAsync(server);
            }
            await Task.WhenAll(loadTasks).ConfigureAwait(false);
            return loadTasks[0].Result.Hash;
        }

        /// <inheritdoc />
        public async Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            var key = grainId.ToString();

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
            var key = grainId.ToString();
            var newEtag = Guid.NewGuid().ToString();

            RedisValue payload = default;
            RedisResult writeWithScriptResponse = null;
            try
            {
                payload = new RedisValue(_grainStorageSerializer.Serialize<T>(grainState.State).ToString());
                writeWithScriptResponse = await WriteToRedisUsingPreparedScriptAsync(payload,
                        etag: etag,
                        key: key,
                        newEtag: newEtag)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(
                    "Failed to write grain state for {GrainType} grain with ID: {GrainId} with redis key {Key}.",
                    grainType,
                    grainId,
                    key);
                throw new RedisStorageException(
                    Invariant($"Failed to write grain state for {grainType} grain with ID: {grainId} with redis key {key}."), e);
            }

            if (writeWithScriptResponse is not null && writeWithScriptResponse.IsNull)
            {
                throw new InconsistentStateException($"ETag mismatch - tried with ETag: {grainState.ETag}");
            }

            grainState.ETag = newEtag;
        }

        private Task<RedisResult> WriteToRedisUsingPreparedScriptAsync(RedisValue payload, string etag, string key, string newEtag)
        {
            var keys = new RedisKey[] { key };
            var args = new RedisValue[] { etag, newEtag, payload };
            return WriteToRedisUsingPreparedScriptAsync(attemptNum: 0);


            async Task<RedisResult> WriteToRedisUsingPreparedScriptAsync(int attemptNum)
            {
                try
                {
                    return await _db.ScriptEvaluateAsync(_preparedWriteScriptHash, keys, args).ConfigureAwait(false);
                }
                catch (RedisServerException rse) when (rse.Message is not null && rse.Message.StartsWith("NOSCRIPT ", StringComparison.Ordinal))
                {
                    // EVALSHA returned error 'NOSCRIPT No matching script. Please use EVAL.'.
                    // This means that SHA1 cache of Lua scripts is cleared at server side, possibly because of Redis server rebooted after Init() method was called. Need to reload Lua script.
                    // Several attempts are made just in case (e.g. if Redis server is rebooted right after previous script reload).
                    if (attemptNum >= ReloadWriteScriptMaxCount)
                    {
                        throw;
                    }

                    await LoadWriteScriptAsync().ConfigureAwait(false);
                    return await WriteToRedisUsingPreparedScriptAsync(attemptNum: attemptNum + 1)
                        .ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            var key = grainId.ToString();
            await _db.KeyDeleteAsync(key).ConfigureAwait(false);
        }

        private async Task Close(CancellationToken cancellationToken)
        {
            if (_connection is null) return;

            await _connection.CloseAsync().ConfigureAwait(false);
            _connection.Dispose();
        }
    }
}