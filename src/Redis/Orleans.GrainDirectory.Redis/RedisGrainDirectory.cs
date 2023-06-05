using System;
using System.Collections.Generic;
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
        private const string DeleteScript =
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

        private readonly RedisGrainDirectoryOptions directoryOptions;
        private readonly ClusterOptions clusterOptions;
        private readonly ILogger<RedisGrainDirectory> logger;
        private readonly RedisKey _keyPrefix;

        private IConnectionMultiplexer redis;
        private IDatabase database;

        public RedisGrainDirectory(
            RedisGrainDirectoryOptions directoryOptions,
            IOptions<ClusterOptions> clusterOptions,
            ILogger<RedisGrainDirectory> logger)
        {
            this.directoryOptions = directoryOptions;
            this.logger = logger;
            this.clusterOptions = clusterOptions.Value;
            _keyPrefix = Encoding.UTF8.GetBytes($"{this.clusterOptions.ClusterId}/directory/");
        }

        public async Task<GrainAddress> Lookup(GrainId grainId)
        {
            try
            {
                var result = (string)await database.StringGetAsync(GetKey(grainId));

                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug("Lookup {GrainId}: {Result}", grainId, string.IsNullOrWhiteSpace(result) ? "null" : result);

                if (string.IsNullOrWhiteSpace(result))
                    return default;

                return JsonSerializer.Deserialize<GrainAddress>(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Lookup failed for {GrainId}", grainId);

                if (IsRedisException(ex))
                    throw new OrleansException($"Lookup failed for {grainId} : {ex.ToString()}");
                else
                    throw;
            }
        }

        public async Task<GrainAddress> Register(GrainAddress address)
        {
            var value = JsonSerializer.Serialize(address);

            try
            {
                var success = await database.StringSetAsync(
                    GetKey(address.GrainId),
                    value,
                    directoryOptions.EntryExpiry,
                    When.NotExists);

                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug("Register {GrainId} ({Address}): {Result}", address.GrainId, value, success ? "OK" : "Conflict");

                if (success)
                    return address;

                return await Lookup(address.GrainId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Register failed for {GrainId} ({Address})", address.GrainId, value);

                if (IsRedisException(ex))
                    throw new OrleansException($"Register failed for {address.GrainId} ({value}) : {ex.ToString()}");
                else
                    throw;
            }
        }

        public async Task Unregister(GrainAddress address)
        {
            try
            {
                var result = (int) await database.ScriptEvaluateAsync(
                    DeleteScript,
                    keys: new RedisKey[] { GetKey(address.GrainId) },
                    values: new RedisValue[] { address.ActivationId.ToParsableString() });

                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug("Unregister {GrainId} ({Address}): {Result}", address.GrainId, JsonSerializer.Serialize(address), (result != 0) ? "OK" : "Conflict");
            }
            catch (Exception ex)
            {
                var value = JsonSerializer.Serialize(address);

                logger.LogError(ex, "Unregister failed for {GrainId} ({Address})", address.GrainId, value);

                if (IsRedisException(ex))
                    throw new OrleansException($"Unregister failed for {address.GrainId} ({value}) : {ex.ToString()}");
                else
                    throw;
            }
        }

        public Task UnregisterSilos(List<SiloAddress> siloAddresses) => Task.CompletedTask;

        public void Participate(ISiloLifecycle lifecycle) => lifecycle.Subscribe(nameof(RedisGrainDirectory), ServiceLifecycleStage.RuntimeInitialize, Initialize, Uninitialize);

        public async Task Initialize(CancellationToken ct = default)
        {
            redis = await directoryOptions.CreateMultiplexer(directoryOptions);

            // Configure logging
            redis.ConnectionRestored += LogConnectionRestored;
            redis.ConnectionFailed += LogConnectionFailed;
            redis.ErrorMessage += LogErrorMessage;
            redis.InternalError += LogInternalError;

            database = redis.GetDatabase();
        }

        private async Task Uninitialize(CancellationToken arg)
        {
            if (redis != null && redis.IsConnected)
            {
                await redis.CloseAsync();
                redis.Dispose();
                redis = null;
                database = null;
            }
        }

        private RedisKey GetKey(GrainId grainId) => _keyPrefix.Append(grainId.ToString());

        #region Logging
        private void LogConnectionRestored(object sender, ConnectionFailedEventArgs e)
            => logger.LogInformation(e.Exception, "Connection to {EndPoint} failed: {FailureType}", e.EndPoint, e.FailureType);

        private void LogConnectionFailed(object sender, ConnectionFailedEventArgs e)
            => logger.LogError(e.Exception, "Connection to {EndPoint} failed: {FailureType}", e.EndPoint, e.FailureType);

        private void LogErrorMessage(object sender, RedisErrorEventArgs e)
            => logger.LogError(e.Message);

        private void LogInternalError(object sender, InternalErrorEventArgs e)
            => logger.LogError(e.Exception, "Internal error");
        #endregion

        // These exceptions are not serializable by the client
        private static bool IsRedisException(Exception ex) => ex is RedisException || ex is RedisTimeoutException || ex is RedisCommandException;
    }
}
