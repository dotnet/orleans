using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
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

        public RedisGrainDirectory(
            RedisGrainDirectoryOptions directoryOptions,
            IOptions<ClusterOptions> clusterOptions,
            ILogger<RedisGrainDirectory> logger)
        {
            this.directoryOptions = directoryOptions;
            this.logger = logger;
            this.clusterOptions = clusterOptions.Value;
        }

        public async Task<GrainAddress> Lookup(string grainId)
        {
            var result = (string) await this.database.StringGetAsync(GetKey(grainId));

            if (this.logger.IsEnabled(LogLevel.Debug))
                this.logger.LogDebug("Lookup {grainId}: {result}", grainId, string.IsNullOrWhiteSpace(result) ? "null" : result);

            if (string.IsNullOrWhiteSpace(result))
                return default;

            return JsonConvert.DeserializeObject<GrainAddress>(result);
        }

        public async Task<GrainAddress> Register(GrainAddress address)
        {
            var value = JsonConvert.SerializeObject(address);
            var success = await this.database.StringSetAsync(
                this.GetKey(address.GrainId),
                value,
                this.directoryOptions.EntryExpiry,
                When.NotExists);

            if (this.logger.IsEnabled(LogLevel.Debug))
                this.logger.LogDebug("Register {grainId} ({address}): {result}", address.GrainId, value, success ? "OK": "Conflict");

            if (success)
                return address;

            return await Lookup(address.GrainId);
        }

        public async Task Unregister(GrainAddress address)
        {
            var key = GetKey(address.GrainId);

            var tx = this.database.CreateTransaction();
            var value = JsonConvert.SerializeObject(address);
            tx.AddCondition(Condition.StringEqual(key, value));
            tx.KeyDeleteAsync(key).Ignore();
            var success = await tx.ExecuteAsync();

            if (this.logger.IsEnabled(LogLevel.Debug))
                this.logger.LogDebug("Unregister {grainId} ({address}): {result}", address.GrainId, value, success ? "OK" : "Conflict");
        }

        public Task UnregisterSilos(List<string> siloAddresses)
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

        private string GetKey(string grainId) => $"{this.clusterOptions.ClusterId}-{grainId}";

        #region Logging
        private void LogConnectionRestored(object sender, ConnectionFailedEventArgs e)
            => this.logger.LogInformation(e.Exception, "Connection to {endpoint) failed: {failureType}", e.EndPoint, e.FailureType);

        private void LogConnectionFailed(object sender, ConnectionFailedEventArgs e)
            => this.logger.LogError(e.Exception, "Connection to {endpoint) failed: {failureType}", e.EndPoint, e.FailureType);

        private void LogErrorMessage(object sender, RedisErrorEventArgs e)
            => this.logger.LogError(e.Message);

        private void LogInternalError(object sender, InternalErrorEventArgs e)
            => this.logger.LogError(e.Exception, "Internal error");
        #endregion
    }
}
