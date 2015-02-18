using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Concurrency;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization;
using Orleans.Storage;
using UnitTestGrainInterfaces;

namespace UnitTestGrains
{
    public interface IPersistenceTestGrainState : IGrainState
    {
        int Field1 { get; set; }
        string Field2 { get; set; }
        SortedDictionary<int, int> SortedDict { get; set; } 
    }

    public interface IPersistenceGenericGrainState<T> : IGrainState
    {
        T Field1 { get; set; }
        string Field2 { get; set; }
        SortedDictionary<T, T> SortedDict { get; set; }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "test1")]
    public class PersistenceTestGrain : Grain<IPersistenceTestGrainState>, IPersistenceTestGrain
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
            IStorageProvider storageProvider = ((ActivationData)Data).StorageProvider;
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
            return State.WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await State.ReadStateAsync();
            return State.Field1;
        }

        public Task<int> GetValue()
        {
            return Task.FromResult(State.Field1);
        }

        public Task DoDelete()
        {
            return State.ClearStateAsync();
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "ErrorInjector")]
    public class PersistenceProviderErrorGrain : Grain<IPersistenceTestGrainState>, IPersistenceProviderErrorGrain
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
            return State.WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await State.ReadStateAsync();
            return State.Field1;
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "ErrorInjector")]
    public class PersistenceUserHandledErrorGrain : Grain<IPersistenceTestGrainState>, IPersistenceUserHandledErrorGrain
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
            var original = State.AsDictionary();
            try
            {
                State.Field1 = val;
                await State.WriteStateAsync();
            }
            catch (Exception exc)
            {
                if (!recover) throw;

                GetLogger().Warn(0, "Grain is handling error in DoWrite - Resetting value to " + original, exc);
                State.SetAll(original);
            }
        }

        public async Task<int> DoRead(bool recover)
        {
            var original = State.AsDictionary();
            try
            {
                await State.ReadStateAsync();
            }
            catch (Exception exc)
            {
                if (!recover) throw;

                GetLogger().Warn(0, "Grain is handling error in DoRead - Resetting value to " + original, exc);
                State.SetAll(original);
            }
            return State.Field1;
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "test1")]
    public class PersistenceErrorGrain : Grain<IPersistenceTestGrainState>, IPersistenceErrorGrain
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
            return State.WriteStateAsync();
        }

        public async Task DoWriteError(int val, bool errorBeforeUpdate)
        {
            if (errorBeforeUpdate) throw new ApplicationException("Before Update");
            State.Field1 = val;
            await State.WriteStateAsync();
            throw new ApplicationException("After Update");
        }

        public async Task<int> DoRead()
        {
            await State.ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public async Task<int> DoReadError(bool errorBeforeRead)
        {
            if (errorBeforeRead) throw new ApplicationException("Before Read");
            await State.ReadStateAsync(); // Attempt to re-read state from store
            throw new ApplicationException("After Read");
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MissingProvider")]
    public class BadProviderTestGrain : Grain<IPersistenceTestGrainState>, IBadProviderTestGrain
    {
        public BadProviderTestGrain()
        {
            GetLogger().Info(1, "Constructor");
        }
        public override Task OnActivateAsync()
        {
            GetLogger().Warn(1, "OnActivateAsync");
            return TaskDone.Done;
        }

        public Task DoSomething()
        {
            GetLogger().Warn(1, "DoSomething");
            throw new ApplicationException("BadProviderTestGrain.DoSomething should never get called when provider is missing");
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "test1")]
    public class PersistenceNoStateTestGrain : Grain, IPersistenceNoStateTestGrain
    {
        public PersistenceNoStateTestGrain()
        {
            GetLogger().Info(1, "Constructor");
        }
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
    public class AzureStorageTestGrain : Grain<IPersistenceTestGrainState>, 
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
            return State.WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await State.ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public Task DoDelete()
        {
            return State.ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle 
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "AzureStore")]
    public class AzureStorageGenericGrain<T> : Grain<IPersistenceGenericGrainState<T>>,
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
            return State.WriteStateAsync();
        }

        public async Task<T> DoRead()
        {
            await State.ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public Task DoDelete()
        {
            return State.ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle 
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "AzureStore")]
    public class AzureStorageTestGrainExtendedKey : Grain<IPersistenceTestGrainState>,
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
            Guid pk = this.GetPrimaryKey(out extKey);
            return Task.FromResult(extKey);
        }

        public Task DoWrite(int val)
        {
            State.Field1 = val;
            return State.WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await State.ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }

        public Task DoDelete()
        {
            return State.ClearStateAsync(); // Automatically marks this grain as DeactivateOnIdle 
        }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    //[Orleans.Providers.StorageProvider(ProviderName = "AzureStorageEmulator")]
    public class MemoryStorageTestGrain : Grain<MemoryStorageTestGrain.INestedPersistenceTestGrainState>, IMemoryStorageTestGrain
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
            return State.WriteStateAsync();
        }

        public async Task<int> DoRead()
        {
            await State.ReadStateAsync(); // Re-read state from store
            return State.Field1;
        }
        public interface INestedPersistenceTestGrainState : IGrainState
        {
            int Field1 { get; set; }
            string Field2 { get; set; }
            SortedDictionary<int, int> SortedDict { get; set; }
        }

    }

    public interface IUserState : IGrainState
    {
        string Name { get; set; }
        string Status { get; set; }
        List<IUser> Friends { get; set; }
    }

    public interface IDerivedUserState : IUserState
    {
        int Field1 { get; set; }
        int Field2 { get; set; }
    }

    /// <summary>
    /// Orleans grain implementation class.
    /// </summary>
    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    //[Orleans.Providers.StorageProvider(ProviderName = "AzureStore")]
    //[Orleans.Providers.StorageProvider(ProviderName = "AzureStorageEmulator")]
    public class UserGrain : Grain<IDerivedUserState>, IUser
    {
        public Task SetName(string name)
        {
            State.Name = name;
            return State.WriteStateAsync();
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
            return State.WriteStateAsync();
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
            StringBuilder sb = new StringBuilder();
            List<Task<string>> promises = new List<Task<string>>();

            foreach (IUser friend in State.Friends)
                promises.Add(friend.GetStatus());

            string[] friends = await Task.WhenAll(promises);

            foreach (var f in friends)
            {
                sb.AppendLine(f);
            }

            return sb.ToString();
        }
    }

    public interface IStateForIReentrentGrain : IGrainState
    {
        int One { get; set; }
        int Two { get; set; }
        Dictionary<string, int> DictOne { get; set; }
        Dictionary<string, int> DictTwo { get; set; }
    }

    [Orleans.Providers.StorageProvider(ProviderName="MemoryStore")]
    [Reentrant]
    public class ReentrentGrainWithState : Grain<IStateForIReentrentGrain>, IReentrentGrainWithState
    {
        private const int Multiple = 100;

        private IReentrentGrainWithState _other;
        private ISchedulingContext _context;
        private TaskScheduler _scheduler;
        private Logger logger;
        private bool executing;

        public override Task OnActivateAsync()
        {
            this._context = RuntimeContext.Current.ActivationContext;
            this._scheduler = TaskScheduler.Current;
            this.logger = GetLogger("ReentrentGrainWithState-" + Identity);
            this.executing = false;
            return base.OnActivateAsync();
        }

        public Task Setup(IReentrentGrainWithState other)
        {
            logger.Info("Setup");
            _other = other;
            return TaskDone.Done;
        }

        public async Task SetOne(int val)
        {
            logger.Info("SetOne");
            CheckRuntimeEnvironment();
            string iStr = val.ToString(CultureInfo.InvariantCulture);
            State.One = val;
            State.DictOne[iStr] = val;
            State.DictTwo[iStr] = val;
            CheckRuntimeEnvironment();
            await State.WriteStateAsync();
            CheckRuntimeEnvironment();
        }

        public async Task SetTwo(int val)
        {
            logger.Info("SetTwo");
            CheckRuntimeEnvironment();
            string iStr = val.ToString(CultureInfo.InvariantCulture);
            State.Two = val;
            State.DictTwo[iStr] = val;
            State.DictOne[iStr] = val;
            CheckRuntimeEnvironment();
            await State.WriteStateAsync();
            CheckRuntimeEnvironment();
        }

        public async Task Test1()
        {
            logger.Info("Test1");
            CheckRuntimeEnvironment();
            for (int i = 1 * Multiple; i < 2 * Multiple; i++)
            {
                Task t1 = this.SetOne(i);
                await t1;
                CheckRuntimeEnvironment();

                Task t2 = State.WriteStateAsync();
                await t2;
                CheckRuntimeEnvironment();

                Task t3 = _other.SetTwo(i);
                await t3;
                CheckRuntimeEnvironment();

                Task t4 = State.WriteStateAsync();
                await t4;
                CheckRuntimeEnvironment();
            }
            CheckRuntimeEnvironment();
        }

        public async Task Test2()
        {
            logger.Info("\n\n\n\n====================================Test2");
            CheckRuntimeEnvironment();
            for (int i = 2 * Multiple; i < 3 * Multiple; i++)
            {
                Task t1 = _other.SetOne(i);
                await t1;
                CheckRuntimeEnvironment();

                Task t2 = State.WriteStateAsync();
                await t2;
                CheckRuntimeEnvironment();

                Task t3 = this.SetTwo(i);
                await t3;
                CheckRuntimeEnvironment();

                Task t4 = State.WriteStateAsync();
                await t4;
                CheckRuntimeEnvironment();
            }
            CheckRuntimeEnvironment();
        }

        public async Task Task_Delay(bool doStart)
        {
            Task wrapper = new Task(async () =>
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
                string errorMsg = String.Format(
                    "Found out that this grain is already in the middle of execution."
                    + " Single threaded-ness violation!"
                    + ".\n{0}",
                    base.CaptureRuntimeEnvironment());
                logger.Error(1, "\n\n\n\n" + errorMsg + "\n\n\n\n");
                throw new Exception(errorMsg);
                //Environment.Exit(1);
            }

            if (RuntimeContext.Current == null || RuntimeContext.Current.ActivationContext == null)
            {
                string errorMsg = String.Format(
                   "Found RuntimeContext.Current == null."
                   + ".\n{0}",
                   base.CaptureRuntimeEnvironment());
                logger.Error(1, "\n\n\n\n" + errorMsg + "\n\n\n\n");
                throw new Exception(errorMsg);
                //Environment.Exit(1);
            }

            var context = RuntimeContext.Current.ActivationContext;
            var scheduler = TaskScheduler.Current;
            StackTrace callStack = new StackTrace();
            
            executing = true;
            Assert.AreEqual(this._scheduler, scheduler, "Wrong TaskScheduler {0} Caller:{1}", scheduler, callStack);
            Assert.IsNotNull(context, "Null ActivationContext -- Expected: {0} Caller:{1}", this._context, callStack);
            Assert.AreEqual(this._context, context, "Wrong ActivationContext {0} Caller:{1}", context, callStack);
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
        private readonly Tuple<string, Logger.Severity>[] overridesOn =
        {
            new Tuple<string, Logger.Severity>("Scheduler", Logger.Severity.Verbose),
            new Tuple<string, Logger.Severity>("Scheduler.ActivationTaskScheduler", Logger.Severity.Verbose3)
        };

        private readonly Tuple<string, Logger.Severity>[] overridesOff =
        {
            new Tuple<string, Logger.Severity>("Scheduler", Logger.Severity.Info),
            new Tuple<string, Logger.Severity>("Scheduler.ActivationTaskScheduler", Logger.Severity.Info)
        };

        public override Task OnActivateAsync()
        {
            _id = _counter++;
            this.executing = false;

            this.logger = GetLogger("NonReentrentStressGrainWithoutState-" + _id);
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
                int nextLevel = level - 1;
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
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < Multiple; i++)
            {
                Log("Test1_ ------>" + i);
                CheckRuntimeEnvironment(String.Format("Test1_{0}_0", i));
                Task task = this.SetOne(i, LEVEL);
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
            Task wrapper = new Task(async () =>
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
            StackTrace callStack = new StackTrace();
            //Log("CheckRuntimeEnvironment - {0} Executing={1}", str, executing);
            if (executing)
            {
                string errorMsg = string.Format(
                    "Found out that grain {0} is already in the middle of execution."
                    + "\n Single threaded-ness violation!"
                    + "\n {1} \n Call Stack={2}",
                    _id, base.CaptureRuntimeEnvironment(), callStack);
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
            string msg = fmt;// +base.CaptureRuntimeEnvironment();
            logger.Info(msg, args);
        }
    }

    public interface IInternalGrainStateData : IGrainState
    {
        int One { get; set; }
    }

    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    internal class InternalGrainWithState : Grain<IInternalGrainStateData>, IInternalGrainWithState
    {
        private Logger logger;

        public override Task OnActivateAsync()
        {
            this.logger = GetLogger("InternalGrainWithState-" + Identity);
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
    public interface IStateInheritanceTestGrainData : IBaseStateData, IGrainState
    {
        int Field2 { get; set; }
    }
    [Orleans.Providers.StorageProvider(ProviderName = "MemoryStore")]
    public class StateInheritanceTestGrain : Grain<IStateInheritanceTestGrainData>, IStateInheritanceTestGrain
    {
        private Logger logger;

        public override Task OnActivateAsync()
        {
            this.logger = GetLogger("StateInheritanceTestGrain-" + Identity);
            logger.Info("OnActivateAsync");
            return base.OnActivateAsync();
        }

        public Task<int> GetValue()
        {
            int val = State.Field1;
            logger.Info("GetValue {0}", val);
            return Task.FromResult(val);
        }

        public Task SetValue(int val)
        {
            State.Field1 = val;
            logger.Info("SetValue {0}", val);
            return State.WriteStateAsync();
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
            Func<int, bool> func2 = (Func<int, bool>)obj2;

            foreach (int val in new[] { _staticFilterValue1, _staticFilterValue2, _staticFilterValue3, _staticFilterValue4 })
            {
                Console.WriteLine("{0} -- Compare value={1}", what, val);
                Assert.AreEqual(func1(val), func2(val), "{0} -- Wrong function after round-trip of {1} with value={2}", what, func1, val);
            }
        }

        private void TestSerializePredicate(string what, Predicate<int> pred1)
        {
            object obj2 = SerializationManager.RoundTripSerializationForTesting(pred1);
            Predicate<int> pred2 = (Predicate<int>)obj2;

            foreach (int val in new[] { _staticFilterValue1, _staticFilterValue2, _staticFilterValue3, _staticFilterValue4 })
            {
                Console.WriteLine("{0} -- Compare value={1}", what, val);
                Assert.AreEqual(pred1(val), pred2(val), "{0} -- Wrong predicate after round-trip of {1} with value={2}", what, pred1, val);
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
