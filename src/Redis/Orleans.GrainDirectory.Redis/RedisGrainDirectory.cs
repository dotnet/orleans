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
                var result = (string)await _database.StringGetAsync(GetKey(grainId));

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
                local cur = redis.call('GET', KEYS[1]) 
                local success = true
                if cur ~= false then
                    local typedCur = cjson.decode(cur)
                    if typedCur.ActivationId ~= ARGV[2] then
                       success = false
                    end
                end

                if (success) then
                    redis.call('SET', KEYS[1], ARGV[1])
                    if ARGV[3] ~= '-1' then
                        redis.call('EXPIRE', KEYS[1], ARGV[3])
                    end 
                    return nil
                end

                return cur
                """;

            var value = JsonSerializer.Serialize(address);
            try
            {
                var previousActivationId = previousAddress is { } ? previousAddress.ActivationId.ToString() : "";
                var key = GetKey(address.GrainId);
                var entryString = (string)await _database.ScriptEvaluateAsync(
                    RegisterScript,
                    keys: new RedisKey[] { key },
                    values: new RedisValue[] { value, previousActivationId, _ttl });

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
                local cur = redis.call('GET', KEYS[1]) 
                if cur ~= false then
                    local typedCur = cjson.decode(cur)
                    if typedCur.ActivationId == ARGV[1] then
                        return redis.call('DEL', KEYS[1])
                    end
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
