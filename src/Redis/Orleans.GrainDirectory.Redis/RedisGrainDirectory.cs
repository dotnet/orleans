using System;
using System.Collections.Generic;
using System.Globalization;
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
        private readonly RedisGrainDirectoryOptions directoryOptions;
        private readonly ClusterOptions clusterOptions;
        private readonly ILogger<RedisGrainDirectory> logger;
        private readonly RedisKey _keyPrefix;
        private readonly string _ttl;
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
            _ttl = directoryOptions.EntryExpiry is { } ts ? ts.TotalSeconds.ToString(CultureInfo.InvariantCulture) : "-1";
        }

        public async Task<GrainAddress> Lookup(GrainId grainId)
        {
            try
            {
                var result = (string)await this.database.StringGetAsync(GetKey(grainId));

                if (this.logger.IsEnabled(LogLevel.Debug))
                    this.logger.LogDebug("Lookup {GrainId}: {Result}", grainId, string.IsNullOrWhiteSpace(result) ? "null" : result);

                if (string.IsNullOrWhiteSpace(result))
                    return default;

                return JsonSerializer.Deserialize<GrainAddress>(result);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Lookup failed for {GrainId}", grainId);

                if (IsRedisException(ex))
                    throw new OrleansException($"Lookup failed for {grainId} : {ex}");
                else
                    throw;
            }
        }

        public Task<GrainAddress> Register(GrainAddress address) => Register(address, null);
        
        public async Task<GrainAddress> Register(GrainAddress address, GrainAddress previousAddress)
        {
            const string UpdateScript =
                """
                local cur = redis.call('GET', KEYS[1]) 
                if (not cur or cur == ARGV[2]) then
                    redis.call('SET', KEYS[1], ARGV[1])
                    if (not ARGV[3] and ARGV[3] ~= '-1') then
                        redis.call('EXPIRE', KEYS[1], ARGV[3])
                    end 
                    return nil 
                end
                return cur
                """;

            var value = JsonSerializer.Serialize(address);
            try
            {
                var previousValue = previousAddress is { } ? JsonSerializer.Serialize(previousAddress) : "";
                var key = GetKey(address.GrainId);
                var args = new RedisValue[] { value, previousValue, _ttl };
                var resultRaw = await this.database.ScriptEvaluateAsync(
                    UpdateScript,
                    keys: new RedisKey[] { key },
                    values: args);
                var result = (string)resultRaw;

                if (this.logger.IsEnabled(LogLevel.Debug))
                {
                    if (result is null)
                    {
                        this.logger.LogDebug("Registered {GrainId} ({Address})", address.GrainId, value);
                    }
                    else
                    {

                        this.logger.LogDebug("Failed to register {GrainId} ({Address}) in directory: Conflicted with existing value, {Result}", address.GrainId, value, result);
                    }
                }

                // This indicates success
                if (result is null)
                {
                    return address;
                }

                // This indicates failure
                return JsonSerializer.Deserialize<GrainAddress>(result);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Failed to register {GrainId} ({Address}) in directory", address.GrainId, value);

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
                if (cur == ARGV[1]) then
                    return redis.call('DEL', KEYS[1])
                end
                return 0
                """;

            try
            {
                var value = JsonSerializer.Serialize(address);
                var result = (int) await this.database.ScriptEvaluateAsync(
                    DeleteScript,
                    keys: new RedisKey[] { GetKey(address.GrainId) },
                    values: new RedisValue[] { value });

                if (this.logger.IsEnabled(LogLevel.Debug))
                    this.logger.LogDebug("Unregister {GrainId} ({Address}): {Result}", address.GrainId, JsonSerializer.Serialize(address), (result != 0) ? "OK" : "Conflict");
            }
            catch (Exception ex)
            {
                var value = JsonSerializer.Serialize(address);

                this.logger.LogError(ex, "Unregister failed for {GrainId} ({Address})", address.GrainId, value);

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
            this.redis = await directoryOptions.CreateMultiplexer(directoryOptions);

            // Configure logging
            this.redis.ConnectionRestored += this.LogConnectionRestored;
            this.redis.ConnectionFailed += this.LogConnectionFailed;
            this.redis.ErrorMessage += this.LogErrorMessage;
            this.redis.InternalError += this.LogInternalError;

            this.database = this.redis.GetDatabase();
        }

        private async Task Uninitialize(CancellationToken arg)
        {
            if (this.redis != null && this.redis.IsConnected)
            {
                await this.redis.CloseAsync();
                this.redis.Dispose();
                this.redis = null;
                this.database = null;
            }
        }

        private RedisKey GetKey(GrainId grainId) => _keyPrefix.Append(grainId.ToString());

        #region Logging
        private void LogConnectionRestored(object sender, ConnectionFailedEventArgs e)
            => this.logger.LogInformation(e.Exception, "Connection to {EndPoint} failed: {FailureType}", e.EndPoint, e.FailureType);

        private void LogConnectionFailed(object sender, ConnectionFailedEventArgs e)
            => this.logger.LogError(e.Exception, "Connection to {EndPoint} failed: {FailureType}", e.EndPoint, e.FailureType);

        private void LogErrorMessage(object sender, RedisErrorEventArgs e)
            => this.logger.LogError(e.Message);

        private void LogInternalError(object sender, InternalErrorEventArgs e)
            => this.logger.LogError(e.Exception, "Internal error");
        #endregion

        // These exceptions are not serializable by the client
        private static bool IsRedisException(Exception ex) => ex is RedisException || ex is RedisTimeoutException || ex is RedisCommandException;
    }
}
