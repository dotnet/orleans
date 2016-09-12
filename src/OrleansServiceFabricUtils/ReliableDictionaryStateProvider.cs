using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace Microsoft.Orleans.ServiceFabric
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.ServiceFabric.Data;
    using Microsoft.ServiceFabric.Data.Collections;

    public class ReliableDictionaryStateProvider : IStorageProvider
    {

        private IProviderRuntime providerRuntime;

        private IReliableStateManager stateManager;

        private IReliableDictionary<string, GrainState> storeInstance;

        public string Name { get; private set; }

        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            this.Log = providerRuntime.GetLogger($"{this.GetType().FullName}/{name}");
            this.Name = name;
            this.providerRuntime = providerRuntime;
            return Task.FromResult(0);
        }

        private async Task<IReliableDictionary<string, GrainState>> GetStore()
        {
            if (storeInstance != null) return storeInstance;
            while (true)
            {
                try
                {
                    this.stateManager = providerRuntime.ServiceProvider.GetRequiredService<IReliableStateManager>();
                    return storeInstance = await this.stateManager.GetOrAddAsync<IReliableDictionary<string, GrainState>>($"grain-state:{this.Name}");
                }
                catch (Exception exception)
                {
                    this.Log.Warn(exception.GetHashCode(), "Exception trying to initialize state manager", exception);
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }

        public Task Close()
        {
            return Task.FromResult(0);
        }

        public Logger Log { get; private set; }

        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var store = await this.GetStore();
            ConditionalValue<GrainState> result;
            using (var tx = this.stateManager.CreateTransaction())
            {
                result = await store.TryGetValueAsync(tx, GetKeyName(grainType, grainReference));
            }

            if (result.HasValue)
            {
                grainState.ETag = result.Value.ETag;
                grainState.State = SerializationManager.DeserializeFromByteArray<object>(result.Value.State)
                                   ?? Activator.CreateInstance(grainState.State.GetType());
            }
            else
            {
                grainState.State = Activator.CreateInstance(grainState.State.GetType());
            }
        }

        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var store = await this.GetStore();
            using (var tx = this.stateManager.CreateTransaction())
            {
                var serializedState = SerializationManager.SerializeToByteArray(grainState.State);
                var version = string.IsNullOrWhiteSpace(grainState.ETag) ? 1 : int.Parse(grainState.ETag) + 1;
                var newGrainState = new GrainState
                {
                    ETag = version.ToString(),
                    State = serializedState
                };

                await store.AddOrUpdateAsync(tx,
                                             GetKeyName(grainType, grainReference),
                                             newGrainState,
                                             (key, existing) =>
                                             {
                                                 var existingVersion = int.Parse(existing.ETag);
                                                 if (existingVersion >= version)
                                                 {
                                                     throw new InconsistentStateException(
                                                         $"Conflict between existing state version {existingVersion} and expected version {version}");
                                                 }

                                                 return newGrainState;
                                             });
            }
        }

        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var store = await this.GetStore();
            using (var tx = this.stateManager.CreateTransaction())
            {
                await store.TryRemoveAsync(tx, GetKeyName(grainType, grainReference));
            }
        }

        private static string GetKeyName(string grainType, GrainReference grainId)
        {
            return $"{grainType}-{grainId.ToKeyString()}";
        }

        [Serializable]
        internal class GrainState : IGrainState
        {
            public byte[] State;

            object IGrainState.State
            {
                get
                {
                    return State;

                }
                set
                {
                    State = (byte[])value;
                }
            }

            public string ETag { get; set; }
        }
    }
}