﻿using Orleans;
using Orleans.MultiCluster;
using Orleans.Providers;
using Orleans.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{

    // use azure storage and a explicitly configured consistency provider
    [OneInstancePerCluster]
    [StorageProvider(ProviderName = "AzureStore")]
    [LogConsistencyProvider(ProviderName = "VersionedStateStorage")]
    public class LogConsistentGrainSharedStorage : LogConsistentGrain
    {
    }

    // use the default storage provider as the shared storage
    [OneInstancePerCluster]
    public class LogConsistentGrainDefaultStorage : LogConsistentGrain
    {
    }

    // use a single-instance log-consistent grain
    [GlobalSingleInstance]
    [StorageProvider(ProviderName = "AzureStore")]
    [LogConsistencyProvider(ProviderName = "VersionedStateStorage")]
    public class GsiLogConsistentGrain : LogConsistentGrain
    {
    }

    // use MemoryStore (which uses GSI grain)
    [OneInstancePerCluster]
    [StorageProvider(ProviderName = "MemoryStore")]
    public class LogConsistentGrainMemoryStorage : LogConsistentGrain
    {
    }

    // use the explictly specified "CustomStorage" log-consistency provider with symmetric access from all clusters
    [OneInstancePerCluster]
    [LogConsistencyProvider(ProviderName = "CustomStorage")]
    public class LogConsistentGrainCustomStorage : LogConsistentGrain,
        Orleans.EventSourcing.CustomVersionedStateStorage.ICustomStorageInterface<MyGrainState, object>
    {

        // we use another impl of this grain as the primary.
        ILogConsistentGrain storagegrain;

        private ILogConsistentGrain GetStorageGrain()
        {
            if (storagegrain == null)
            {
                storagegrain = GrainFactory.GetGrain<ILogConsistentGrain>(this.GetPrimaryKeyLong(), "UnitTests.Grains.LogConsistentGrainSharedStorage");
            }
            return storagegrain;
        }
 

        public Task<bool> ApplyUpdatesToStorageAsync(IReadOnlyList<object> updates, int expectedversion)
        {
            return GetStorageGrain().Update(updates, expectedversion);
        }

        public async Task<KeyValuePair<int, MyGrainState>> ReadStateFromStorageAsync()
        {
            var kvp = await GetStorageGrain().Read();
            return new KeyValuePair<int, MyGrainState>(kvp.Key, (MyGrainState)kvp.Value);
        }
    }

    // use the explictly specified "CustomStorage" log-consistency provider with access from primary cluster only
    [OneInstancePerCluster]
    [LogConsistencyProvider(ProviderName = "CustomStoragePrimaryCluster")]
    public class LogConsistentGrainCustomStoragePrimaryCluster : LogConsistentGrain,
        Orleans.EventSourcing.CustomVersionedStateStorage.ICustomStorageInterface<MyGrainState, object>
    {

        // we use fake in-memory state as the storage
        MyGrainState state;
        int version;

        public Task<bool> ApplyUpdatesToStorageAsync(IReadOnlyList<object> updates, int expectedversion)
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
                state.Apply(u);
                version++;
            }

            return Task.FromResult(true);
        }

        public Task<KeyValuePair<int, MyGrainState>> ReadStateFromStorageAsync()
        {
            if (state == null)
            {
                state = new MyGrainState();
                version = 0;
            }
            return Task.FromResult(new KeyValuePair<int, MyGrainState>(version, (MyGrainState)SerializationManager.DeepCopy(state)));
        }
    }


}
