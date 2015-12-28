using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Concurrency;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [Serializable]
    public class PersistenceTestGrainState
    {
        public PersistenceTestGrainState()
        {
            SortedDict = new SortedDictionary<int, int>();
        }

        public int Field1 { get; set; }
        public string Field2 { get; set; }
        public SortedDictionary<int, int> SortedDict { get; set; }
    }

    [Serializable]
    public class PersistenceGenericGrainState<T>
    {
        public T Field1 { get; set; }
        public string Field2 { get; set; }
        public SortedDictionary<T, T> SortedDict { get; set; }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "test1")]
    public class PersistenceTestGrain : Grain<PersistenceTestGrainState>, IPersistenceTestGrain
    {
        public override Task OnActivateAsync()
        {
            return TaskDone.Done;
        }

        public Task<bool> CheckStateInit()
        {
            Assert.IsNotNull(State, "Null State");
            Assert.AreEqual(0, State.Field1, "Field1 = {0}", State.Field1);
            Assert.IsNull(State.Field2, "Field2 = {0}", State.Field2);
            //Assert.IsNotNull(State.Field3, "Null Field3");
            //Assert.AreEqual(0, State.Field3.Count, "Field3 = {0}", String.Join("'", State.Field3));
            Assert.IsNotNull(State.SortedDict, "Null SortedDict");
            return Task.FromResult(true);
        }

        public Task<string> CheckProviderType()
        {
            var storageProvider = ((ActivationData) Data).StorageProvider;
            Assert.IsNotNull(storageProvider, "Null storage provider");
            return Task.FromResult(storageProvider.GetType().FullName);
        }

        public Task DoSomething()
        {
            return TaskDone.Done;
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            State.SortedDict[val] = val;
            return WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await ReadStateAsync();
            return State.Field1;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public async Task DoDelete()
        {
            await ClearStateAsync();
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "ErrorInjector")]
    public class PersistenceProviderErrorGrain : Grain<PersistenceTestGrainState>, IPersistenceProviderErrorGrain
    {
        public override Task OnActivateAsync()
        {
            return TaskDone.Done;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await ReadStateAsync();
            return State.Field1;
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "ErrorInjector")]
    public class PersistenceUserHandledErrorGrain : Grain<PersistenceTestGrainState>, IPersistenceUserHandledErrorGrain
    {
        public override Task OnActivateAsync()
        {
            return TaskDone.Done;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public async Task DoWrite(int val, bool recover)
        {
            var original = SerializationManager.DeepCopy(State);
            try
            {
                State.Field1 = val;
                await WriteStateAsync();
            }
            catch (Exception exc)
            {
                if (!recover) throw;

                GetLogger().Warn(0, "Grain is handling error in DoWrite - Resetting value to " + original, exc);
                State = (PersistenceTestGrainState)original;
            }
        }

        public async Task<int> DoRead(bool recover)
        {
            var original = SerializationManager.DeepCopy(State);
            try
            {
                await ReadStateAsync();
            }
            catch (Exception exc)
            {
                if (!recover) throw;

                GetLogger().Warn(0, "Grain is handling error in DoRead - Resetting value to " + original, exc);
                State = (PersistenceTestGrainState)original;
            }
            return State.Field1;
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "test1")]
    public class PersistenceErrorGrain : Grain<PersistenceTestGrainState>, IPersistenceErrorGrain
    {
        public override Task OnActivateAsync()
        {
            return TaskDone.Done;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task DoWriteError(int val, bool errorBeforeUpdate)
        {
            if (errorBeforeUpdate) throw new ApplicationException("Before Update");
            State.Field1 = val;
            await WriteStateAsync();
            throw new ApplicationException("After Update");
        }

        public async Task<int> DoRead()
        {
            await ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public async Task<int> DoReadError(bool errorBeforeRead)
        {
            if (errorBeforeRead) throw new ApplicationException("Before Read");
            await ReadStateAsync(); // Attempt to re-read state from store
            throw new ApplicationException("After Read");
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MissingProvider")]
    public class BadProviderTestGrain : Grain<PersistenceTestGrainState>, IBadProviderTestGrain
    {
        public override Task OnActivateAsync()
        {
            GetLogger().Warn(1, "OnActivateAsync");
            return TaskDone.Done;
        }

        public Task DoSomething()
        {
            GetLogger().Warn(1, "DoSomething");
            throw new ApplicationException(
                "BadProviderTestGrain.DoSomething should never get called when provider is missing");
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "test1")]
    public class PersistenceNoStateTestGrain : Grain, IPersistenceNoStateTestGrain
    {
        public override Task OnActivateAsync()
        {
            GetLogger().Info(1, "OnActivateAsync");
            return TaskDone.Done;
        }

        public Task DoSomething()
        {
            GetLogger().Info(1, "DoSomething");
            return TaskDone.Done;
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "AzureStore")]
    public class AzureStorageTestGrain : Grain<PersistenceTestGrainState>,
        IAzureStorageTestGrain, IAzureStorageTestGrain_LongKey
    {
        public override Task OnActivateAsync()
        {
            return TaskDone.Done;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public Task DoDelete()
        {
            return ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle 
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "AzureStore")]
    public class AzureStorageGenericGrain<T> : Grain<PersistenceGenericGrainState<T>>,
        IAzureStorageGenericGrain<T>
    {
        public override Task OnActivateAsync()
        {
            return TaskDone.Done;
        }

        public Task<T> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task DoWrite(T val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task<T> DoRead()
        {
            await ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public Task DoDelete()
        {
            return ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle 
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "AzureStore")]
    public class AzureStorageTestGrainExtendedKey : Grain<PersistenceTestGrainState>,
        IAzureStorageTestGrain_GuidExtendedKey, IAzureStorageTestGrain_LongExtendedKey
    {
        public override Task OnActivateAsync()
        {
            return TaskDone.Done;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task<string> GetExtendedKeyValue()
        {
            string extKey;
            var pk = this.GetPrimaryKey(out extKey);
            return Task.FromResult(extKey);
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public Task DoDelete()
        {
            return ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle 
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    //[Orleans.Providers.StorageProvider(ProviderName = "AzureStorageEmulator")]
    public class MemoryStorageTestGrain : Grain<MemoryStorageTestGrain.NestedPersistenceTestGrainState>,
        IMemoryStorageTestGrain
    {
        public override Task OnActivateAsync()
        {
            return TaskDone.Done;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            return WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        [Serializable]
        public class NestedPersistenceTestGrainState
        {
            public int Field1 { get; set; }
            public string Field2 { get; set; }
            public SortedDictionary<int, int> SortedDict { get; set; }
        }
    }

    [Serializable]
    public class UserState
    {
        public UserState()
        {
            Friends = new List<IUser>();
        }

        public string Name { get; set; }
        public string Status { get; set; }
        public List<IUser> Friends { get; set; }
    }

    [Serializable]
    public class DerivedUserState : UserState
    {
        public int Field1 { get; set; }
        public int Field2 { get; set; }
    }

    /// <summary>
    /// Orleans grain implementation class.
    /// </summary>
    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    //[Orleans.Providers.StorageProvider(ProviderName = "AzureStore")]
    //[Orleans.Providers.StorageProvider(ProviderName = "AzureStorageEmulator")]
    public class UserGrain : Grain<DerivedUserState>, IUser
    {
        public Task SetName(string name)
        {
            State.Name = name;
            return WriteStateAsync();
        }

        public Task<string> GetStatus()
        {
            return Task.FromResult(String.Format("{0} : {1}", State.Name, State.Status));
        }

        public Task<string> GetName()
        {
            return Task.FromResult(State.Name);
        }

        public Task UpdateStatus(string status)
        {
            State.Status = status;
            return WriteStateAsync();
        }

        public Task AddFriend(IUser friend)
        {
            if (!State.Friends.Contains(friend))
                State.Friends.Add(friend);
            else
                throw new Exception("Already a friend.");

            return TaskDone.Done;
        }

        public Task<List<IUser>> GetFriends()
        {
            return Task.FromResult(State.Friends);
        }

        public async Task<string> GetFriendsStatuses()
        {
            var sb = new StringBuilder();
            var promises = new List<Task<string>>();

            foreach (var friend in State.Friends)
                promises.Add(friend.GetStatus());

            var friends = await Task.WhenAll(promises);

            foreach (var f in friends)
            {
                sb.AppendLine(f);
            }

            return sb.ToString();
        }
    }

    [Serializable]
    public class StateForIReentrentGrain
    {
        public StateForIReentrentGrain()
        {
            DictOne = new Dictionary<string, int>();
            DictTwo = new Dictionary<string, int>();
        }

        public int One { get; set; }
        public int Two { get; set; }
        public Dictionary<string, int> DictOne { get; set; }
        public Dictionary<string, int> DictTwo { get; set; }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    [Reentrant]
    public class ReentrentGrainWithState : Grain<StateForIReentrentGrain>, IReentrentGrainWithState
    {
        private const int Multiple = 100;

        private IReentrentGrainWithState _other;
        private ISchedulingContext _context;
        private TaskScheduler _scheduler;
        private Logger logger;
        private bool executing;
        private Task outstandingWriteStateOperation;

        public override Task OnActivateAsync()
        {
            _context = RuntimeContext.Current.ActivationContext;
            _scheduler = TaskScheduler.Current;
            logger = GetLogger("ReentrentGrainWithState-" + Identity);
            executing = false;
            return base.OnActivateAsync();
        }

        // When reentrant grain is doing WriteStateAsync, etag violations are posssible due to concurent writes.
        // The solution is to serialize all writes, and make sure only a single write is outstanind at any moment in time.
        // No deadlocks are posssible with that approach, since all WriteStateAsync go to the store, which does not issue call into grains,
        // thus cycle of calls is not posssible.
        // Implementaton: need to use While and not if, due to the same "early check becomes later invalid" standard problem, like in conditional variables.
        private async Task PerformSerializedStateUpdate()
        {
            while (outstandingWriteStateOperation != null)
            {
                await outstandingWriteStateOperation;
            }
            outstandingWriteStateOperation = WriteStateAsync();
            await outstandingWriteStateOperation;
            outstandingWriteStateOperation = null;
        }

        public Task Setup(IReentrentGrainWithState other)
        {
            logger.Info("Setup");
            _other = other;
            return TaskDone.Done;
        }

        public async Task SetOne(int val)
        {
            logger.Info("SetOne Start");
            CheckRuntimeEnvironment();
            var iStr = val.ToString(CultureInfo.InvariantCulture);
            State.One = val;
            State.DictOne[iStr] = val;
            State.DictTwo[iStr] = val;
            CheckRuntimeEnvironment();
            await PerformSerializedStateUpdate();
            CheckRuntimeEnvironment();
        }

        public async Task SetTwo(int val)
        {
            logger.Info("SetTwo Start");
            CheckRuntimeEnvironment();
            var iStr = val.ToString(CultureInfo.InvariantCulture);
            State.Two = val;
            State.DictTwo[iStr] = val;
            State.DictOne[iStr] = val;
            CheckRuntimeEnvironment();
            await PerformSerializedStateUpdate();
            CheckRuntimeEnvironment();
        }

        public async Task Test1()
        {
            logger.Info(" ==================================== Test1 Started");
            CheckRuntimeEnvironment();
            for (var i = 1*Multiple; i < 2*Multiple; i++)
            {
                var t1 = SetOne(i);
                await t1;
                CheckRuntimeEnvironment();

                var t2 = PerformSerializedStateUpdate();
                await t2;
                CheckRuntimeEnvironment();

                var t3 = _other.SetTwo(i);
                await t3;
                CheckRuntimeEnvironment();

                var t4 = PerformSerializedStateUpdate();
                await t4;
                CheckRuntimeEnvironment();
            }
            CheckRuntimeEnvironment();
            logger.Info(" ==================================== Test1 Done");
        }

        public async Task Test2()
        {
            logger.Info("==================================== Test2 Started");
            CheckRuntimeEnvironment();
            for (var i = 2*Multiple; i < 3*Multiple; i++)
            {
                var t1 = _other.SetOne(i);
                await t1;
                CheckRuntimeEnvironment();

                var t2 = PerformSerializedStateUpdate();
                await t2;
                CheckRuntimeEnvironment();

                var t3 = SetTwo(i);
                await t3;
                CheckRuntimeEnvironment();

                var t4 = PerformSerializedStateUpdate();
                await t4;
                CheckRuntimeEnvironment();
            }
            CheckRuntimeEnvironment();
            logger.Info(" ==================================== Test2 Done");
        }

        public async Task Task_Delay(bool doStart)
        {
            var wrapper = new Task(async () =>
            {
                logger.Info("Before Task.Delay #1 TaskScheduler.Current=" + TaskScheduler.Current);
                await DoDelay(1);
                logger.Info("After Task.Delay #1 TaskScheduler.Current=" + TaskScheduler.Current);
                await DoDelay(2);
                logger.Info("After Task.Delay #2 TaskScheduler.Current=" + TaskScheduler.Current);
            });

            if (doStart)
            {
                wrapper.Start(); // THIS IS THE KEY STEP!
            }

            await wrapper;
        }

        private async Task DoDelay(int i)
        {
            logger.Info("Before Task.Delay #{0} TaskScheduler.Current={1}", i, TaskScheduler.Current);
            await Task.Delay(1);
            logger.Info("After Task.Delay #{0} TaskScheduler.Current={1}", i, TaskScheduler.Current);
        }

        private void CheckRuntimeEnvironment()
        {
            if (executing)
            {
                var errorMsg = String.Format(
                    "Found out that this grain is already in the middle of execution."
                    + " Single threaded-ness violation!"
                    + ".\n{0}",
                    CaptureRuntimeEnvironment());
                logger.Error(1, "\n\n\n\n" + errorMsg + "\n\n\n\n");
                throw new Exception(errorMsg);
                //Environment.Exit(1);
            }

            if (RuntimeContext.Current == null || RuntimeContext.Current.ActivationContext == null)
            {
                var errorMsg = String.Format(
                    "Found RuntimeContext.Current == null."
                    + ".\n{0}",
                    CaptureRuntimeEnvironment());
                logger.Error(1, "\n\n\n\n" + errorMsg + "\n\n\n\n");
                throw new Exception(errorMsg);
                //Environment.Exit(1);
            }

            var context = RuntimeContext.Current.ActivationContext;
            var scheduler = TaskScheduler.Current;
            var callStack = new StackTrace();

            executing = true;
            Assert.AreEqual(_scheduler, scheduler, "Wrong TaskScheduler {0} Caller:{1}", scheduler, callStack);
            Assert.IsNotNull(context, "Null ActivationContext -- Expected: {0} Caller:{1}", _context, callStack);
            Assert.AreEqual(_context, context, "Wrong ActivationContext {0} Caller:{1}", context, callStack);
            executing = false;
        }
    }

    public class NonReentrentStressGrainWithoutState : Grain, INonReentrentStressGrainWithoutState
    {
        private const int Multiple = 100;
        private Logger logger;
        private bool executing;
        private const int LEVEL = 2; // level 2 is enough to repro the problem.

        private static int _counter = 1;
        private int _id;

        // HACK for testing
        private readonly Tuple<string, Severity>[] overridesOn =
        {
            new Tuple<string, Severity>("Scheduler", Severity.Verbose),
            new Tuple<string, Severity>("Scheduler.ActivationTaskScheduler", Severity.Verbose3)
        };

        private readonly Tuple<string, Severity>[] overridesOff =
        {
            new Tuple<string, Severity>("Scheduler", Severity.Info),
            new Tuple<string, Severity>("Scheduler.ActivationTaskScheduler", Severity.Info)
        };

        public NonReentrentStressGrainWithoutState()
        {
        }

        public NonReentrentStressGrainWithoutState(IGrainIdentity identity, IGrainRuntime runtime)
            : base(identity, runtime)
        {
        }

        public override Task OnActivateAsync()
        {
            _id = _counter++;
            executing = false;

            logger = GetLogger("NonReentrentStressGrainWithoutState-" + _id);
            Log("--> OnActivateAsync");
//#if DEBUG
//            // HACK for testing
//            Logger.SetTraceLevelOverrides(overridesOn.ToList());
//#endif
            Log("<-- OnActivateAsync");
            return base.OnActivateAsync();
        }

        private async Task SetOne(int iter, int level)
        {
            Log(String.Format("---> SetOne {0}-{1}_0", iter, level));
            CheckRuntimeEnvironment("SetOne");
            if (level > 0)
            {
                Log("SetOne {0}-{1}_1. Before await Task.Done.", iter, level);
                await TaskDone.Done;
                Log("SetOne {0}-{1}_2. After await Task.Done.", iter, level);
                CheckRuntimeEnvironment(String.Format("SetOne {0}-{1}_3", iter, level));
                Log("SetOne {0}-{1}_4. Before await Task.Delay.", iter, level);
                await Task.Delay(TimeSpan.FromMilliseconds(10));
                Log("SetOne {0}-{1}_5. After await Task.Delay.", iter, level);
                CheckRuntimeEnvironment(String.Format("SetOne {0}-{1}_6", iter, level));
                var nextLevel = level - 1;
                await SetOne(iter, nextLevel);
                Log("SetOne {0}-{1}_7 => {2}. After await SetOne call.", iter, level, nextLevel);
                CheckRuntimeEnvironment(String.Format("SetOne {0}-{1}_8", iter, level));
                Log("SetOne {0}-{1}_9. Finished SetOne.", iter, level);
            }
            CheckRuntimeEnvironment(String.Format("SetOne {0}-{1}_10", iter, level));
            Log("<--- SetOne {0}-{1}_11", iter, level);
        }

        public async Task Test1()
        {
            Log(String.Format("Test1.Start"));

            CheckRuntimeEnvironment("Test1.BeforeLoop");
            var tasks = new List<Task>();
            for (var i = 0; i < Multiple; i++)
            {
                Log("Test1_ ------>" + i);
                CheckRuntimeEnvironment(String.Format("Test1_{0}_0", i));
                var task = SetOne(i, LEVEL);
                Log("After SetOne call " + i);
                CheckRuntimeEnvironment(String.Format("Test1_{0}_1", i));
                tasks.Add(task);
                CheckRuntimeEnvironment(String.Format("Test1_{0}_2", i));
                Log("Test1_ <------" + i);
            }
            CheckRuntimeEnvironment("Test1.AfterLoop");
            Log(String.Format("Test1_About to WhenAll"));
            await Task.WhenAll(tasks);
            Log(String.Format("Test1.Finish"));
            CheckRuntimeEnvironment("Test1.Finish-CheckRuntimeEnvironment");
//#if DEBUG
//            // HACK for testing
//            Logger.SetTraceLevelOverrides(overridesOff.ToList());
//#endif
        }

        public async Task Task_Delay(bool doStart)
        {
            var wrapper = new Task(async () =>
            {
                logger.Info("Before Task.Delay #1 TaskScheduler.Current=" + TaskScheduler.Current);
                await DoDelay(1);
                logger.Info("After Task.Delay #1 TaskScheduler.Current=" + TaskScheduler.Current);
                await DoDelay(2);
                logger.Info("After Task.Delay #2 TaskScheduler.Current=" + TaskScheduler.Current);
            });

            if (doStart)
            {
                wrapper.Start(); // THIS IS THE KEY STEP!
            }

            await wrapper;
        }

        private async Task DoDelay(int i)
        {
            logger.Info("Before Task.Delay #{0} TaskScheduler.Current={1}", i, TaskScheduler.Current);
            await Task.Delay(1);
            logger.Info("After Task.Delay #{0} TaskScheduler.Current={1}", i, TaskScheduler.Current);
        }

        private void CheckRuntimeEnvironment(string str)
        {
            var callStack = new StackTrace();
            //Log("CheckRuntimeEnvironment - {0} Executing={1}", str, executing);
            if (executing)
            {
                var errorMsg = string.Format(
                    "Found out that grain {0} is already in the middle of execution."
                    + "\n Single threaded-ness violation!"
                    + "\n {1} \n Call Stack={2}",
                    _id, CaptureRuntimeEnvironment(), callStack);
                logger.Error(1, "\n\n\n\n" + errorMsg + "\n\n\n\n");
                OrleansTaskScheduler.Instance.DumpSchedulerStatus();
                TraceLogger.Flush();
                //Environment.Exit(1);
                throw new Exception(errorMsg);
            }
            //Assert.IsFalse(executing, "Found out that this grain is already in the middle of execution. Single threaded-ness violation!");
            executing = true;
            //Log("CheckRuntimeEnvironment - Start sleep " + str);
            Thread.Sleep(10);
            executing = false;
            //Log("CheckRuntimeEnvironment - End sleep " + str);
        }


        private void Log(string fmt, params object[] args)
        {
            var msg = fmt; // +base.CaptureRuntimeEnvironment();
            logger.Info(msg, args);
        }
    }

    [Serializable]
    public class InternalGrainStateData
    {
        public int One { get; set; }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    internal class InternalGrainWithState : Grain<InternalGrainStateData>, IInternalGrainWithState
    {
        private Logger logger;

        public override Task OnActivateAsync()
        {
            logger = GetLogger("InternalGrainWithState-" + Identity);
            return base.OnActivateAsync();
        }

        public Task SetOne(int val)
        {
            logger.Info("SetOne");
            State.One = val;
            return TaskDone.Done;
        }
    }

    public interface IBaseStateData // Note: I am deliberately not using IGrainState here.
    {
        int Field1 { get; set; }
    }

    [Serializable]
    public class StateInheritanceTestGrainData : IBaseStateData
    {
        private int Field2 { get; set; }

        public int Field1 { get; set; }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    public class StateInheritanceTestGrain : Grain<StateInheritanceTestGrainData>, IStateInheritanceTestGrain
    {
        private Logger logger;

        public override Task OnActivateAsync()
        {
            logger = GetLogger("StateInheritanceTestGrain-" + Identity);
            logger.Info("OnActivateAsync");
            return base.OnActivateAsync();
        }

        public Task<int> GetValue()
        {
            var val = State.Field1;
            logger.Info("GetValue {0}", val);
            return Task.FromResult(val);
        }

        public Task SetValue(int val)
        {
            State.Field1 = val;
            logger.Info("SetValue {0}", val);
            return WriteStateAsync();
        }
    }

    public class SerializationTestGrain : Grain, ISerializationTestGrain
    {
        private static int _staticFilterValue1 = 41;
        private static int _staticFilterValue2 = 42;
        private static int _staticFilterValue3 = 43;
        private static int _staticFilterValue4 = 44;

        private readonly int _instanceFilterValue2 = _staticFilterValue2;

        public Task Test_Serialize_Func()
        {
            Func<int, bool> staticFilterFunc = i => i == _staticFilterValue3;

            //int instanceFilterValue2 = _staticFilterValue2;
            //
            //Func<int, bool> instanceFilterFunc = i => i == instanceFilterValue2;

            Func<int, bool> staticFuncInGrainClass = PredFuncStatic;
            //Func<int, bool> instanceFuncInGrainClass = this.PredFuncInstance;

            // Works OK
            TestSerializeFuncPtr("Func Lambda - Static field", staticFilterFunc);
            TestSerializeFuncPtr("Static Func In Grain Class", staticFuncInGrainClass);

            // Fails
            //TestSerializeFuncPtr("Instance Func In Grain Class", instanceFuncInGrainClass);
            //TestSerializeFuncPtr("Func Lambda - Instance field", instanceFilterFunc);

            return TaskDone.Done;
        }

        public Task Test_Serialize_Predicate()
        {
            Predicate<int> staticPredicate = i => i == _staticFilterValue2;

            //int instanceFilterValue2 = _staticFilterValue2;
            //
            //Predicate<int> instancePredicate = i => i == instanceFilterValue2;

            // Works OK
            TestSerializePredicate("Predicate Lambda - Static field", staticPredicate);

            // Fails
            //TestSerializePredicate("Predicate Lambda - Instance field", instancePredicate);

            return TaskDone.Done;
        }

        public Task Test_Serialize_Predicate_Class()
        {
            IMyPredicate pred = new MyPredicate(_staticFilterValue2);

            // Works OK
            TestSerializePredicate("Predicate Class Instance", pred.FilterFunc);

            return TaskDone.Done;
        }

        public Task Test_Serialize_Predicate_Class_Param(IMyPredicate pred)
        {
            // Works OK
            TestSerializePredicate("Predicate Class Instance passed as param", pred.FilterFunc);

            return TaskDone.Done;
        }

        // Utility methods

        private void TestSerializeFuncPtr(string what, Func<int, bool> func1)
        {
            object obj2 = SerializationManager.RoundTripSerializationForTesting(func1);
            var func2 = (Func<int, bool>) obj2;

            foreach (
                var val in new[] {_staticFilterValue1, _staticFilterValue2, _staticFilterValue3, _staticFilterValue4})
            {
                Console.WriteLine("{0} -- Compare value={1}", what, val);
                Assert.AreEqual(func1(val), func2(val), "{0} -- Wrong function after round-trip of {1} with value={2}",
                    what, func1, val);
            }
        }

        private void TestSerializePredicate(string what, Predicate<int> pred1)
        {
            object obj2 = SerializationManager.RoundTripSerializationForTesting(pred1);
            var pred2 = (Predicate<int>) obj2;

            foreach (
                var val in new[] {_staticFilterValue1, _staticFilterValue2, _staticFilterValue3, _staticFilterValue4})
            {
                Console.WriteLine("{0} -- Compare value={1}", what, val);
                Assert.AreEqual(pred1(val), pred2(val), "{0} -- Wrong predicate after round-trip of {1} with value={2}",
                    what, pred1, val);
            }
        }

        public bool PredFuncInstance(int i)
        {
            return i == _instanceFilterValue2;
        }

        public static bool PredFuncStatic(int i)
        {
            return i == _staticFilterValue2;
        }
    }
}