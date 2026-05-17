using Orleans.Providers;
using Orleans.Serialization;
using Orleans.Journaling;
using Microsoft.Extensions.DependencyInjection;
using UnitTests.GrainInterfaces;

#pragma warning disable ORLEANSEXP005
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

    [LogConsistencyProvider(ProviderName = "JournaledState")]
    public class LogTestGrainJournaledStateStorage : LogTestGrain
    {
    }

    [LogConsistencyProvider(ProviderName = "JournaledState")]
    public class LogTestGrainJournaledStateStorageWithAuxiliaryState(
        [FromKeyedServices("auxiliary-state")] IDurableValue<int> auxiliaryState)
        : LogTestGrain,
            ILogTestGrainWithAuxiliaryState
    {
        public Task<int> GetAuxiliaryValue()
        {
            return Task.FromResult(auxiliaryState.Value);
        }

        public Task SetAuxiliaryValue(int value)
        {
            auxiliaryState.Value = value;
            return Task.CompletedTask;
        }
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

        public Task ClearStoredState()
        {
            return GetStorageGrain().Clear();
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
        public override async Task OnActivateAsync(CancellationToken cancellationToken)
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

        public Task ClearStoredState()
        {
            state = null;
            version = 0;
            return Task.CompletedTask;
        }
    }


}
#pragma warning restore ORLEANSEXP005
