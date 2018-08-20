using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Data.Collections;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace Orleans.Clustering.ServiceFabric.Stateful
{
    internal class ReliableDictionaryGrainStorage : IGrainStorage
    {
        private readonly string name;
        private readonly IReliableStateManager stateManager;
        private readonly SerializationManager serializationManager;
        private readonly ReliableDictionaryGrainStorageOptions options;
        private IReliableDictionary<string, byte[]> stateDictionary;
        private readonly AsyncLock asyncLock = new AsyncLock();

        public ReliableDictionaryGrainStorage(
            string name,
            IReliableStateManager stateManager,
            IOptions<ReliableDictionaryGrainStorageOptions> options,
            SerializationManager serializationManager)
        {
            this.name = name ?? throw new ArgumentNullException(nameof(name));
            this.stateManager = stateManager;
            this.serializationManager = serializationManager;
            this.options = options.Value;
        }
        
        private ValueTask<IReliableDictionary<string, byte[]>> GetStorage()
        {
            if (this.stateDictionary != null) return new ValueTask<IReliableDictionary<string, byte[]>>(this.stateDictionary);

            return Async();

            async ValueTask<IReliableDictionary<string, byte[]>> Async()
            {
                using (await asyncLock.LockAsync())
                {
                    if (this.stateDictionary == null)
                    {
                        this.stateDictionary = await this.stateManager.GetOrAddAsync<IReliableDictionary<string, byte[]>>(this.options.StateName ?? this.name);
                    }

                    return this.stateDictionary;
                }
            }
        }
        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var keyString = grainReference.GrainId.ToParsableString();
            using (var tx = this.stateManager.CreateTransaction())
            {
                var storage = await this.GetStorage();
                var result = await storage.TryGetValueAsync(tx, keyString);

                if (result.HasValue)
                {
                    grainState.State = this.serializationManager.DeserializeFromByteArray<object>(result.Value);
                }

                await tx.CommitAsync();
            }
        }

        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var keyString = grainReference.GrainId.ToParsableString();
            using (var tx = this.stateManager.CreateTransaction())
            {
                var storage = await this.GetStorage();
                var bytes = this.serializationManager.SerializeToByteArray(grainState.State);
                await storage.SetAsync(tx, keyString, bytes);
                await tx.CommitAsync();
            }
        }

        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var keyString = grainReference.GrainId.ToParsableString();
            using (var tx = this.stateManager.CreateTransaction())
            {
                var storage = await this.GetStorage();
                await storage.TryRemoveAsync(tx, keyString);
                await tx.CommitAsync();
            }
        }
    }
}
