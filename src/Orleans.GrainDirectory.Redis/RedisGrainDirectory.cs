using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

        private ConnectionMultiplexer redis;
        private IDatabase database;

        public RedisGrainDirectory(
            RedisGrainDirectoryOptions directoryOptions,
            IOptions<ClusterOptions> clusterOptions)
        {
            this.directoryOptions = directoryOptions;
            this.clusterOptions = clusterOptions.Value;
        }

        public async Task<GrainAddress> Lookup(string grainId)
        {
            var result = (string) await this.database.StringGetAsync(GetKey(grainId));

            if (string.IsNullOrWhiteSpace(result))
                return default;

            return JsonConvert.DeserializeObject<GrainAddress>(result);
        }

        public async Task<GrainAddress> Register(GrainAddress address)
        {
            var success = await this.database.StringSetAsync(
                GetKey(address.GrainId),
                JsonConvert.SerializeObject(address),
                this.directoryOptions.EntryExpiry,
                When.NotExists);

            if (success)
                return address;

            return await Lookup(address.GrainId);
        }

        public async Task Unregister(GrainAddress address)
        {
            var key = GetKey(address.GrainId);

            var tx = this.database.CreateTransaction();
            tx.AddCondition(Condition.StringEqual(key, JsonConvert.SerializeObject(address)));
            tx.KeyDeleteAsync(key).Ignore();
            await tx.ExecuteAsync();
        }

        public Task UnregisterSilos(List<string> siloAddresses)
        {
            return Task.CompletedTask;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe(nameof(RedisGrainDirectory), ServiceLifecycleStage.RuntimeInitialize, Initialize);
        }

        public async Task Initialize(CancellationToken ct = default)
        {
            this.redis = await ConnectionMultiplexer.ConnectAsync(directoryOptions.ConfigurationOptions);
            this.database = redis.GetDatabase();
        }

        private string GetKey(string grainId)
        {
            return $"{this.clusterOptions.ClusterId}-{grainId}";
        }
    }
}
