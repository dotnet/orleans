using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;

namespace UnitTests.StorageTests
{
    [DebuggerDisplay("MockStorageProvider:{Name}")]
    public class MockStorageProvider : IControllable, IStorageProvider
    {
        public enum Commands
        {
            InitCount,
            SetValue,
            GetProvideState,
            SetErrorInjection,
            GetLastState,
            ResetHistory
        }
        [Serializable]
        public class StateForTest 
        {
            public int InitCount { get; set; }
            public int CloseCount { get; set; }
            public int ReadCount { get; set; }
            public int WriteCount { get; set; }
            public int DeleteCount { get; set; }
        }

        private static int _instanceNum;
        private readonly int _id;

        private int initCount, closeCount, readCount, writeCount, deleteCount;

        private readonly int numKeys;
        private ILocalDataStore StateStore;
        private SerializationManager serializationManager;
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

        public StateForTest GetProviderState()
        {
            var state = new StateForTest();
            state.InitCount = initCount;
            state.CloseCount = closeCount;
            state.DeleteCount = deleteCount;
            state.ReadCount = readCount;
            state.WriteCount = writeCount;
            return state;
        }

        [Serializable]
        public class SetValueArgs
        {
            public Type StateType { get; set; }
            public string GrainType { get; set; }
            public GrainReference GrainReference { get; set; }
            public string Name { get; set; }
            public object Val { get; set; }

        }

        public void SetValue(SetValueArgs args)
        {
            SetValue(args.StateType, args.GrainType, args.GrainReference, args.Name, args.Val);
        }

        private void SetValue(Type stateType, string grainType, GrainReference grainReference, string name, object val)
        {
            lock (StateStore)
            {
                Log.Info("Setting stored value {0} for {1} to {2}", name, grainReference, val);
                var keys = MakeGrainStateKeys(grainType, grainReference);
                var storedDict = StateStore.ReadRow(keys);
                if (!storedDict.ContainsKey(stateStoreKey))
                {
                    storedDict[stateStoreKey] = Activator.CreateInstance(stateType);
                } 

                var storedState = storedDict[stateStoreKey];
                var field = storedState.GetType().GetProperty(name).GetSetMethod(true);
                field.Invoke(storedState, new[] { val });
                LastId = GetId(grainReference);
                LastState = storedState;
            }
        }

        public object GetLastState()
        {
            return LastState;
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
            this.serializationManager = providerRuntime.ServiceProvider.GetRequiredService<SerializationManager>();
            Interlocked.Increment(ref initCount);
            
            //blocked by port HierarchicalKeyStore to coreclr
            StateStore = new HierarchicalKeyStore(numKeys);
            
            Log.Info(0, "Finished Init Name={0} Config={1}", name, config);
            return Task.CompletedTask;
        }

        public virtual Task Close()
        {
            Log.Info(0, "Close");
            Interlocked.Increment(ref closeCount);
            StateStore.Clear();
            return Task.CompletedTask;
        }

        public virtual Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            Log.Info(0, "ReadStateAsync for {0} {1}", grainType, grainReference);
            Interlocked.Increment(ref readCount);
            lock (StateStore)
            {
                var storedState = GetLastState(grainType, grainReference, grainState);
                grainState.State = this.serializationManager.DeepCopy(storedState); // Read current state data
            }
            return Task.CompletedTask;
        }

        public virtual Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            Log.Info(0, "WriteStateAsync for {0} {1}", grainType, grainReference);
            Interlocked.Increment(ref writeCount);
            lock (StateStore)
            {
                var storedState = this.serializationManager.DeepCopy(grainState.State); // Store current state data
                var stateStore = new Dictionary<string, object> {{ stateStoreKey, storedState }};
                StateStore.WriteRow(MakeGrainStateKeys(grainType, grainReference), stateStore, grainState.ETag);

                LastId = GetId(grainReference);
                LastState = storedState;
            }
            return Task.CompletedTask;
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
            return Task.CompletedTask;
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

        public void ResetHistory()
        {
            // initCount = 0;
            closeCount = readCount = writeCount = deleteCount = 0;
            LastId = null;
            LastState = null;
        }

        #region IControllable interface methods
        /// <summary>
        /// A function to execute a control command.
        /// </summary>
        /// <param name="command">A serial number of the command.</param>
        /// <param name="arg">An opaque command argument</param>
        public virtual Task<object> ExecuteCommand(int command, object arg)
        {
            switch ((Commands)command)
            {
                case Commands.InitCount:
                    return Task.FromResult<object>(initCount);
                case Commands.SetValue:
                    SetValue((SetValueArgs) arg);
                    return Task.FromResult<object>(true); 
                case Commands.GetProvideState:
                    return Task.FromResult<object>(GetProviderState());
                case Commands.GetLastState:
                    return Task.FromResult(GetLastState());
                case Commands.ResetHistory:
                    ResetHistory();
                    return Task.FromResult<object>(true);
                default:
                    return Task.FromResult<object>(true); 
            }
        }
        #endregion
    }
}
