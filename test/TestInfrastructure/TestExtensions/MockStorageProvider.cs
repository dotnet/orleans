using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

namespace UnitTests.StorageTests
{
    public static class SiloBuilderExtensions
    {
        public static ISiloBuilder AddTestStorageProvider<T>(this ISiloBuilder builder, string name) where T : IGrainStorage
        {
            return builder.AddTestStorageProvider(name, (sp, n) => ActivatorUtilities.CreateInstance<T>(sp));
        }

        public static ISiloBuilder AddTestStorageProvider<T>(this ISiloBuilder builder, string name, Func<IServiceProvider, string, T> createInstance) where T : IGrainStorage
        {
            return builder.ConfigureServices(services =>
            {
                services.AddSingletonNamedService<IGrainStorage>(name, (sp, n) => createInstance(sp, n));

                if (typeof(ILifecycleParticipant<ISiloLifecycle>).IsAssignableFrom(typeof(T)))
                {
                    services.AddSingletonNamedService(name, (svc, n) => (ILifecycleParticipant<ISiloLifecycle>)svc.GetRequiredServiceByName<IGrainStorage>(name));
                }

                if (typeof(IControllable).IsAssignableFrom(typeof(T)))
                {
                    services.AddSingletonNamedService(name, (svc, n) => (IControllable)svc.GetRequiredServiceByName<IGrainStorage>(name));
                }
            });
        }
    }

    [DebuggerDisplay("MockStorageProvider:{Name}")]
    public class MockStorageProvider : IControllable, IGrainStorage
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
        [GenerateSerializer]
        public class StateForTest 
        {
            [Id(0)]
            public int InitCount { get; set; }
            [Id(1)]
            public int CloseCount { get; set; }
            [Id(2)]
            public int ReadCount { get; set; }
            [Id(3)]
            public int WriteCount { get; set; }
            [Id(4)]
            public int DeleteCount { get; set; }
        }

        private static int _instanceNum;
        private readonly int _id;

        private int initCount, closeCount, readCount, writeCount, deleteCount;

        private readonly int numKeys;
        private readonly DeepCopier copier;
        private ILocalDataStore StateStore;
        private const string stateStoreKey = "State";
        private ILogger logger;
        public string LastId { get; private set; }
        public object LastState { get; private set; }

        public string Name { get; private set; }

        public MockStorageProvider(ILoggerFactory loggerFactory, DeepCopier copier)
            : this(Guid.NewGuid().ToString(), 2, loggerFactory, copier)
        { }

        public MockStorageProvider(string name, ILoggerFactory loggerFactory, DeepCopier copier)
            : this(name, 2, loggerFactory, copier)
        { }

        public MockStorageProvider(string name, int numKeys, ILoggerFactory loggerFactory, DeepCopier copier)
        {
            _id = ++_instanceNum;
            this.numKeys = numKeys;
            this.copier = copier;
            this.Name = name;
            this.logger = loggerFactory.CreateLogger(string.Format("Storage.{0}-{1}", this.GetType().Name, this._id));

            logger.Info(0, "Init Name={0}", name);
            Interlocked.Increment(ref initCount);

            StateStore = new HierarchicalKeyStore(numKeys);

            logger.Info(0, "Finished Init Name={0}", name);
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
        [GenerateSerializer]
        public class SetValueArgs
        {
            [Id(0)]
            public Type StateType { get; set; }
            [Id(1)]
            public string GrainType { get; set; }
            [Id(2)]
            public GrainReference GrainReference { get; set; }
            [Id(3)]
            public string Name { get; set; }
            [Id(4)]
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
                this.logger.Info("Setting stored value {0} for {1} to {2}", name, grainReference, val);
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

        private object GetLastState<T>(string grainType, GrainReference grainReference, IGrainState<T> grainState)
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

        public virtual Task Close()
        {
            logger.Info(0, "Close");
            Interlocked.Increment(ref closeCount);
            StateStore.Clear();
            return Task.CompletedTask;
        }

        public virtual Task ReadStateAsync<T>(string grainType, GrainReference grainReference, IGrainState<T> grainState)
        {
            logger.Info(0, "ReadStateAsync for {0} {1}", grainType, grainReference);
            Interlocked.Increment(ref readCount);
            lock (StateStore)
            {
                var storedState = GetLastState(grainType, grainReference, grainState);
                grainState.RecordExists = storedState != null;
                grainState.State = (T)this.copier.Copy(storedState); // Read current state data
            }
            return Task.CompletedTask;
        }

        public virtual Task WriteStateAsync<T>(string grainType, GrainReference grainReference, IGrainState<T> grainState)
        {
            logger.Info(0, "WriteStateAsync for {0} {1}", grainType, grainReference);
            Interlocked.Increment(ref writeCount);
            lock (StateStore)
            {
                var storedState = this.copier.Copy(grainState.State); // Store current state data
                var stateStore = new Dictionary<string, object> {{ stateStoreKey, storedState }};
                StateStore.WriteRow(MakeGrainStateKeys(grainType, grainReference), stateStore, grainState.ETag);

                LastId = GetId(grainReference);
                LastState = storedState;
                grainState.RecordExists = true;
            }
            return Task.CompletedTask;
        }

        public virtual Task ClearStateAsync<T>(string grainType, GrainReference grainReference, IGrainState<T> grainState)
        {
            logger.Info(0, "ClearStateAsync for {0} {1}", grainType, grainReference);
            Interlocked.Increment(ref deleteCount);
            var keys = MakeGrainStateKeys(grainType, grainReference);
            lock (StateStore)
            {
                StateStore.DeleteRow(keys, grainState.ETag);
                LastId = GetId(grainReference);
                LastState = null;
            }
            grainState.RecordExists = false;
            return Task.CompletedTask;
        }

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
    }
}
