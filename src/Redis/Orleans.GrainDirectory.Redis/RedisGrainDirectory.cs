using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Orleans.GrainDirectory.Redis
{
    public class RedisGrainDirectory : IGrainDirectory, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly RedisGrainDirectoryOptions _directoryOptions;
        private readonly ClusterOptions _clusterOptions;
        private readonly ILogger<RedisGrainDirectory> _logger;
        private readonly RedisKey _keyPrefix;
        private readonly string _ttl;
        private IConnectionMultiplexer _redis;
        private IDatabase _database;

        public RedisGrainDirectory(
            RedisGrainDirectoryOptions directoryOptions,
            IOptions<ClusterOptions> clusterOptions,
            ILogger<RedisGrainDirectory> logger)
        {
            _directoryOptions = directoryOptions;
            _logger = logger;
            _clusterOptions = clusterOptions.Value;
            _keyPrefix = Encoding.UTF8.GetBytes($"{_clusterOptions.ClusterId}/directory/");
            _ttl = directoryOptions.EntryExpiry is { } ts ? ts.TotalSeconds.ToString(CultureInfo.InvariantCulture) : "-1";
        }

        public async Task<GrainAddress> Lookup(GrainId grainId)
        {
            try
            {
                var result = (string)await _database.HashGetAsync(GetKey(grainId), "data");

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Lookup {GrainId}: {Result}", grainId, string.IsNullOrWhiteSpace(result) ? "null" : result);

                if (string.IsNullOrWhiteSpace(result))
                    return default;

                return JsonSerializer.Deserialize<GrainAddress>(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lookup failed for {GrainId}", grainId);

                if (IsRedisException(ex))
                    throw new OrleansException($"Lookup failed for {grainId} : {ex}");
                else
                    throw;
            }
        }

        public Task<GrainAddress> Register(GrainAddress address) => Register(address, null);
        
        public async Task<GrainAddress> Register(GrainAddress address, GrainAddress previousAddress)
        {
            const string RegisterScript =
                """
                local etag = redis.call('HGET', KEYS[1], 'etag') 
                local data = redis.call('HGET', KEYS[1], 'data') 
                if (not etag or etag == ARGV[2]) then
                    redis.call('HSET', KEYS[1], 'data', ARGV[1])
                    redis.call('HSET', KEYS[1], 'etag', ARGV[4])

                    if (not ARGV[3] and ARGV[3] ~= '-1') then
                        redis.call('EXPIRE', KEYS[1], ARGV[3])
                    end 
                    return nil 
                end
                return data
                """;

            var value = JsonSerializer.Serialize(address);
            try
            {
                var etag = previousAddress is { } ? previousAddress.ActivationId.ToString() : "";
                var newEtag = address.ActivationId.ToString();
                var key = GetKey(address.GrainId);
                var args = new RedisValue[] { value, etag, _ttl, newEtag };
                var entryString = (string)await _database.ScriptEvaluateAsync(
                    RegisterScript,
                    keys: new RedisKey[] { key },
                    values: args);

                if (entryString is null)
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug("Registered {GrainId} ({Address})", address.GrainId, value);
                    }

                    return address;
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Failed to register {GrainId} ({Address}) in directory: Conflicted with existing value, {Result}", address.GrainId, value, entryString);
                }

                return JsonSerializer.Deserialize<GrainAddress>(entryString);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register {GrainId} ({Address}) in directory", address.GrainId, value);

                if (IsRedisException(ex))
                {
                    throw new OrleansException($"Register failed for {address.GrainId} ({value}) : {ex}");
                }
                else
                {
                    throw;
                }
            }
        }

        public async Task Unregister(GrainAddress address)
        {
            const string DeleteScript =
                """
                local etag = redis.call('HGET', KEYS[1], 'etag') 
                if (etag == ARGV[1]) then
                    return redis.call('DEL', KEYS[1])
                end
                return 0
                """;

            try
            {
                var value = JsonSerializer.Serialize(address);
                var result = (int) await _database.ScriptEvaluateAsync(
                    DeleteScript,
                    keys: new RedisKey[] { GetKey(address.GrainId) },
                    values: new RedisValue[] { address.ActivationId.ToString() });

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Unregister {GrainId} ({Address}): {Result}", address.GrainId, JsonSerializer.Serialize(address), (result != 0) ? "OK" : "Conflict");
                }
            }
            catch (Exception ex)
            {
                var value = JsonSerializer.Serialize(address);

                _logger.LogError(ex, "Unregister failed for {GrainId} ({Address})", address.GrainId, value);

                if (IsRedisException(ex))
                    throw new OrleansException($"Unregister failed for {address.GrainId} ({value}) : {ex}");
                else
                    throw;
            }
        }

        public Task UnregisterSilos(List<SiloAddress> siloAddresses)
        {
            return Task.CompletedTask;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(nameof(RedisGrainDirectory), ServiceLifecycleStage.RuntimeInitialize, Initialize, Uninitialize);
        }

        public async Task Initialize(CancellationToken ct = default)
        {
            _redis = await _directoryOptions.CreateMultiplexer(_directoryOptions);

            // Configure logging
            _redis.ConnectionRestored += LogConnectionRestored;
            _redis.ConnectionFailed += LogConnectionFailed;
            _redis.ErrorMessage += LogErrorMessage;
            _redis.InternalError += LogInternalError;

            _database = _redis.GetDatabase();
        }

        private async Task Uninitialize(CancellationToken arg)
        {
            if (_redis != null && _redis.IsConnected)
            {
                await _redis.CloseAsync();
                _redis.Dispose();
                _redis = null;
                _database = null;
            }
        }

        private RedisKey GetKey(GrainId grainId) => _keyPrefix.Append(grainId.ToString());

        #region Logging
        private void LogConnectionRestored(object sender, ConnectionFailedEventArgs e)
            => _logger.LogInformation(e.Exception, "Connection to {EndPoint} failed: {FailureType}", e.EndPoint, e.FailureType);

        private void LogConnectionFailed(object sender, ConnectionFailedEventArgs e)
            => _logger.LogError(e.Exception, "Connection to {EndPoint} failed: {FailureType}", e.EndPoint, e.FailureType);

        private void LogErrorMessage(object sender, RedisErrorEventArgs e)
            => _logger.LogError(e.Message);

        private void LogInternalError(object sender, InternalErrorEventArgs e)
            => _logger.LogError(e.Exception, "Internal error");
        #endregion

        // These exceptions are not serializable by the client
        private static bool IsRedisException(Exception ex) => ex is RedisException || ex is RedisTimeoutException || ex is RedisCommandException;
    }
}
