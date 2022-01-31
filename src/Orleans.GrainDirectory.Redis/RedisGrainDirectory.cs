using System;
using System.Collections.Generic;
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

        private ConnectionMultiplexer redis;
        private IDatabase database;
        private LuaScript deleteScript;

        public RedisGrainDirectory(
            RedisGrainDirectoryOptions directoryOptions,
            IOptions<ClusterOptions> clusterOptions,
            ILogger<RedisGrainDirectory> logger)
        {
            this.directoryOptions = directoryOptions;
            this.logger = logger;
            this.clusterOptions = clusterOptions.Value;
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
                var success = await this.database.StringSetAsync(
                    this.GetKey(address.GrainId),
                    value,
                    this.directoryOptions.EntryExpiry,
                    When.NotExists);

                if (this.logger.IsEnabled(LogLevel.Debug))
                    this.logger.LogDebug("Register {GrainId} ({Address}): {Result}", address.GrainId, value, success ? "OK" : "Conflict");

                if (success)
                    return address;

                return await Lookup(address.GrainId);
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "Register failed for {GrainId} ({Address})", address.GrainId, value);

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
                var result = (int) await this.database.ScriptEvaluateAsync(this.deleteScript, new { key = GetKey(address.GrainId), val = address.ActivationId });

                if (this.logger.IsEnabled(LogLevel.Debug))
                    this.logger.LogDebug("Unregister {GrainId} ({Address}): {Result}", address.GrainId, JsonSerializer.Serialize(address), (result != 0) ? "OK" : "Conflict");
            }
            catch (Exception ex)
            {
                var value = JsonSerializer.Serialize(address);

                this.logger.LogError(ex, "Unregister failed for {GrainId} ({Address})", address.GrainId, value);

                if (IsRedisException(ex))
                    throw new OrleansException($"Unregister failed for {address.GrainId} ({value}) : {ex.ToString()}");
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
            this.redis = await ConnectionMultiplexer.ConnectAsync(this.directoryOptions.ConfigurationOptions);

            // Configure logging
            this.redis.ConnectionRestored += this.LogConnectionRestored;
            this.redis.ConnectionFailed += this.LogConnectionFailed;
            this.redis.ErrorMessage += this.LogErrorMessage;
            this.redis.InternalError += this.LogInternalError;
            this.redis.IncludeDetailInExceptions = true;

            this.database = this.redis.GetDatabase();

            this.deleteScript = LuaScript.Prepare(
    @"	
local cur = redis.call('GET', @key)
if cur ~= false then
    local typedCur = cjson.decode(cur)
    if typedCur.ActivationId == @val  then	
        return redis.call('DEL', @key)	
    end
end
return 0	
                ");
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

        private string GetKey(GrainId grainId) => $"{this.clusterOptions.ClusterId}-{grainId}";

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
