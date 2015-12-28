using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace UnitTests.StorageTests
{
    [DebuggerDisplay("MockStorageProvider:{Name}")]
    public class MockStorageProvider : MarshalByRefObject, IStorageProvider
    {
        private static int _instanceNum;
        private readonly int _id;

        private int initCount, closeCount, readCount, writeCount, deleteCount;

        public int InitCount { get { return initCount; } }
        public int CloseCount { get { return closeCount; } }
        public int ReadCount { get { return readCount; } }
        public int WriteCount { get { return writeCount; } }
        public int DeleteCount { get { return deleteCount; } }

        private readonly int numKeys;
        private ILocalDataStore StateStore;
        private const string stateStoreKey = "State";

        public string LastId { get; private set; }
        public object LastState { get; private set; }

        public string Name { get; private set; }
        public Logger Log { get; protected set; }

        public MockStorageProvider()
            : this(2)
        { }
        public MockStorageProvider(int numKeys)
        {
            _id = ++_instanceNum;
            this.numKeys = numKeys;
        }

        public virtual void SetValue<TState>(string grainType, GrainReference grainReference, string name, object val)
        {
            lock (StateStore)
            {
                Log.Info("Setting stored value {0} for {1} to {2}", name, grainReference, val);
                var keys = MakeGrainStateKeys(grainType, grainReference);
                var storedDict = StateStore.ReadRow(keys);
                if (!storedDict.ContainsKey(stateStoreKey))
                {
                    storedDict[stateStoreKey] = Activator.CreateInstance<TState>();
                } 

                var storedState = storedDict[stateStoreKey];
                var field = storedState.GetType().GetProperty(name).GetSetMethod(true);
                field.Invoke(storedState, new[] { val });
                LastId = GetId(grainReference);
                LastState = storedState;
            }
        }

        public T GetLastState<T>()
        {
            return (T) LastState;
        }

        private object GetLastState(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            lock (StateStore)
            {
                var keys = MakeGrainStateKeys(grainType, grainReference);
                var storedStateRow = StateStore.ReadRow(keys);
                if (!storedStateRow.ContainsKey(stateStoreKey))
                {
                    storedStateRow[stateStoreKey] = Activator.CreateInstance(grainState.State.GetType());
                }

                var storedState = storedStateRow[stateStoreKey];
                LastId = GetId(grainReference);
                LastState = storedState;
                return storedState;
            }
        }

        #region IStorageProvider methods
        public virtual Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            this.Name = name;
            string loggerName = string.Format("Storage.{0}-{1}", this.GetType().Name, _id);
            Log = providerRuntime.GetLogger(loggerName);

            Log.Info(0, "Init Name={0} Config={1}", name, config);
            Interlocked.Increment(ref initCount);

            if (LocalDataStoreInstance.LocalDataStore != null)
            {
                // Attached to shared local key store
                StateStore = LocalDataStoreInstance.LocalDataStore;
            }
            else
            {
                StateStore = new HierarchicalKeyStore(numKeys);
            }

            Log.Info(0, "Finished Init Name={0} Config={1}", name, config);
            return TaskDone.Done;
        }

        public virtual Task Close()
        {
            Log.Info(0, "Close");
            Interlocked.Increment(ref closeCount);
            StateStore.Clear();
            return TaskDone.Done;
        }

        public virtual Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            Log.Info(0, "ReadStateAsync for {0} {1}", grainType, grainReference);
            Interlocked.Increment(ref readCount);
            lock (StateStore)
            {
                var storedState = GetLastState(grainType, grainReference, grainState);
                grainState.State = SerializationManager.DeepCopy(storedState); // Read current state data
            }
            return TaskDone.Done;
        }

        public virtual Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            Log.Info(0, "WriteStateAsync for {0} {1}", grainType, grainReference);
            Interlocked.Increment(ref writeCount);
            lock (StateStore)
            {
                var storedState = SerializationManager.DeepCopy(grainState.State); // Store current state data
                var stateStore = new Dictionary<string, object> {{ stateStoreKey, storedState }};
                StateStore.WriteRow(MakeGrainStateKeys(grainType, grainReference), stateStore, grainState.ETag);

                LastId = GetId(grainReference);
                LastState = storedState;
            }
            return TaskDone.Done;
        }

        public virtual Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            Log.Info(0, "ClearStateAsync for {0} {1}", grainType, grainReference);
            Interlocked.Increment(ref deleteCount);
            var keys = MakeGrainStateKeys(grainType, grainReference);
            lock (StateStore)
            {
                StateStore.DeleteRow(keys, grainState.ETag);
                LastId = GetId(grainReference);
                LastState = null;
            }
            return TaskDone.Done;
        }
        #endregion

        private static string GetId(GrainReference grainReference)
        {
            return grainReference.ToKeyString();
        }
        private static IList<Tuple<string, string>> MakeGrainStateKeys(string grainType, GrainReference grainReference)
        {
            return new[]
            {
                Tuple.Create("GrainType", grainType),
                Tuple.Create("GrainId", GetId(grainReference))
            }.ToList();
        }
    }
}
