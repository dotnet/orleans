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
            this.logger = loggerFactory.CreateLogger($"Storage.{this.GetType().Name}-{this._id}");

            logger.LogInformation("Init Name={Name}", name);
            Interlocked.Increment(ref initCount);

            StateStore = new HierarchicalKeyStore(numKeys);

            logger.LogInformation("Finished Init Name={Name}", name);
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
            public GrainId GrainId { get; set; }
            [Id(3)]
            public string Name { get; set; }
            [Id(4)]
            public object Val { get; set; }

        }

        public void SetValue(SetValueArgs args)
        {
            SetValue(args.StateType, args.GrainType, args.GrainId, args.Name, args.Val);
        }

        private void SetValue(Type stateType, string grainType, GrainId grainId, string name, object val)
        {
            lock (StateStore)
            {
                this.logger.LogInformation("Setting stored value {Name} for {GrainId} to {Value}", name, grainId, val);
                var keys = MakeGrainStateKeys(grainType, grainId);
                var storedDict = StateStore.ReadRow(keys);
                if (!storedDict.ContainsKey(stateStoreKey))
                {
                    storedDict[stateStoreKey] = Activator.CreateInstance(stateType);
                } 

                var storedState = storedDict[stateStoreKey];
                var field = storedState.GetType().GetProperty(name).GetSetMethod(true);
                field.Invoke(storedState, new[] { val });
                LastId = GetId(grainId);
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

        private object GetLastState<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            lock (StateStore)
            {
                var keys = MakeGrainStateKeys(grainType, grainId);
                var storedStateRow = StateStore.ReadRow(keys);
                if (!storedStateRow.ContainsKey(stateStoreKey))
                {
                    storedStateRow[stateStoreKey] = Activator.CreateInstance(grainState.State.GetType());
                }

                var storedState = storedStateRow[stateStoreKey];
                LastId = GetId(grainId);
                LastState = storedState;
                return storedState;
            }
        }

        public virtual Task Close()
        {
            logger.LogInformation("Close");
            Interlocked.Increment(ref closeCount);
            StateStore.Clear();
            return Task.CompletedTask;
        }

        public virtual Task ReadStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            logger.LogInformation("ReadStateAsync for {GrainType} {GrainId}", grainType, grainId);
            Interlocked.Increment(ref readCount);
            lock (StateStore)
            {
                var storedState = GetLastState(grainType, grainId, grainState);
                grainState.RecordExists = storedState != null;
                grainState.State = (T)this.copier.Copy(storedState); // Read current state data
            }
            return Task.CompletedTask;
        }

        public virtual Task WriteStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            logger.LogInformation("WriteStateAsync for {GrainType} {GrainId}", grainType, grainId);
            Interlocked.Increment(ref writeCount);
            lock (StateStore)
            {
                var storedState = this.copier.Copy(grainState.State); // Store current state data
                var stateStore = new Dictionary<string, object> {{ stateStoreKey, storedState }};
                StateStore.WriteRow(MakeGrainStateKeys(grainType, grainId), stateStore, grainState.ETag);

                LastId = GetId(grainId);
                LastState = storedState;
                grainState.RecordExists = true;
            }
            return Task.CompletedTask;
        }

        public virtual Task ClearStateAsync<T>(string grainType, GrainId grainId, IGrainState<T> grainState)
        {
            logger.LogInformation("ClearStateAsync for {GrainType} {GrainId}", grainType, grainId);
            Interlocked.Increment(ref deleteCount);
            var keys = MakeGrainStateKeys(grainType, grainId);
            lock (StateStore)
            {
                StateStore.DeleteRow(keys, grainState.ETag);
                LastId = GetId(grainId);
                LastState = null;
            }
            grainState.RecordExists = false;
            return Task.CompletedTask;
        }

        private static string GetId(GrainId grainId) => grainId.ToString();

        private static IList<Tuple<string, string>> MakeGrainStateKeys(string grainType, GrainId grainId)
        {
            return new[]
            {
                Tuple.Create("GrainType", grainType),
                Tuple.Create("GrainId", GetId(grainId))
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
