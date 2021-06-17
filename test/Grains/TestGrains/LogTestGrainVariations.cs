using Orleans;
using Orleans.Providers;
using Orleans.Serialization;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;

namespace TestGrains
{
    // variations of the log consistent grain are used to test a variety of provider and configurations

    // use azure storage and a explicitly configured consistency provider
    [StorageProvider(ProviderName = "AzureStore")]
    [LogConsistencyProvider(ProviderName = "StateStorage")]
    public class LogTestGrainSharedStateStorage : LogTestGrain
    {
    }

    // use azure storage and a explicitly configured consistency provider
    [StorageProvider(ProviderName = "AzureStore")]
    [LogConsistencyProvider(ProviderName = "LogStorage")]
    public class LogTestGrainSharedLogStorage : LogTestGrain
    {
    }

    // use the default storage provider as the shared storage
    public class LogTestGrainDefaultStorage : LogTestGrain
    {
    }

    // use MemoryStore (which uses GSI grain)
    [StorageProvider(ProviderName = "MemoryStore")]
    public class LogTestGrainMemoryStorage : LogTestGrain
    {
    }

    // use the explictly specified "CustomStorage" log-consistency provider with symmetric access from all clusters
    [LogConsistencyProvider(ProviderName = "CustomStorage")]
    public class LogTestGrainCustomStorage : LogTestGrain,
        Orleans.EventSourcing.CustomStorage.ICustomStorageInterface<MyGrainState, object>
    {

        // we use another impl of this grain as the primary.
        private ILogTestGrain storagegrain;

        private ILogTestGrain GetStorageGrain()
        {
            if (storagegrain == null)
            {
                storagegrain = GrainFactory.GetGrain<ILogTestGrain>(this.GetPrimaryKeyLong(), "TestGrains.LogTestGrainSharedStateStorage");
            }
            return storagegrain;
        }
 

        public Task<bool> ApplyUpdatesToStorage(IReadOnlyList<object> updates, int expectedversion)
        {
            return GetStorageGrain().Update(updates, expectedversion);
        }

        public async Task<KeyValuePair<int, MyGrainState>> ReadStateFromStorage()
        {
            var kvp = await GetStorageGrain().Read();
            return new KeyValuePair<int, MyGrainState>(kvp.Key, (MyGrainState)kvp.Value);
        }
    }

    // use the explictly specified "CustomStorage" log-consistency provider with access from primary cluster only
    [LogConsistencyProvider(ProviderName = "CustomStoragePrimaryCluster")]
    public class LogTestGrainCustomStoragePrimaryCluster : LogTestGrain,
        Orleans.EventSourcing.CustomStorage.ICustomStorageInterface<MyGrainState, object>
    {
        private readonly DeepCopier<MyGrainState> copier;

        // we use fake in-memory state as the storage
        private MyGrainState state;
        private int version;

        public LogTestGrainCustomStoragePrimaryCluster(DeepCopier<MyGrainState> copier)
        {
            this.copier = copier;
        }

        // simulate an async call during activation. This caused deadlock in earlier version,
        // so I add it here to catch regressions.
        public override async Task OnActivateAsync()
        {
            await Task.Run(async () =>
            {
                await Task.Delay(10);
            });
        }


        public Task<bool> ApplyUpdatesToStorage(IReadOnlyList<object> updates, int expectedversion)
        {
            if (state == null)
            {
                state = new MyGrainState();
                version = 0;
            }

            if (expectedversion != version)
                return Task.FromResult(false);

            foreach (var u in updates)
            {
                this.TransitionState(state, u);
                version++;
            }

            return Task.FromResult(true);
        }

        public Task<KeyValuePair<int, MyGrainState>> ReadStateFromStorage()
        {
            if (state == null)
            {
                state = new MyGrainState();
                version = 0;
            }
            return Task.FromResult(new KeyValuePair<int, MyGrainState>(version, this.copier.Copy(state)));
        }
    }


}
