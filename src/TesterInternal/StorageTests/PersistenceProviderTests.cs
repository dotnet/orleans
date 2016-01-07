using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Storage;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using Samples.StorageProviders;
using UnitTests.Tester;

namespace UnitTests.StorageTests
{
    [TestClass]
    [DeploymentItem("ClientConfigurationForTesting.xml")]
    public class PersistenceProviderTests_Local
    {
        public TestContext TestContext { get; set; }

        StorageProviderManager storageProviderManager;
        readonly Dictionary<string, string> providerCfgProps = new Dictionary<string, string>();

        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            Console.WriteLine("ClassInitialize {0}", testContext.TestName);
            testContext.WriteLine("ClassInitialize");
            BufferPool.InitGlobalBufferPool(new MessagingConfiguration(false));
            ClientConfiguration cfg = ClientConfiguration.LoadFromFile("ClientConfigurationForTesting.xml");
            testContext.WriteLine(cfg.ToString());
            TraceLogger.Initialize(cfg);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
        }

        [TestInitialize]
        public void TestInitialize()
        {
            TestContext.WriteLine("TestInitialize");
            storageProviderManager = new StorageProviderManager(new GrainFactory(), new DefaultServiceProvider());
            storageProviderManager.LoadEmptyStorageProviders(new ClientProviderRuntime(new GrainFactory(), new DefaultServiceProvider())).WaitWithThrow(TestConstants.InitTimeout);
            providerCfgProps.Clear();
            SerializationManager.InitializeForTesting();
            LocalDataStoreInstance.LocalDataStore = null;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            TestContext.WriteLine("TestCleanup");
            LocalDataStoreInstance.LocalDataStore = null;
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task PersistenceProvider_Mock_WriteRead()
        {
            string testName = TestContext.TestName;

            IStorageProvider store = new MockStorageProvider();
            var cfg = new ProviderConfiguration(providerCfgProps, null);
            await store.Init(testName, storageProviderManager, cfg);

            await Test_PersistenceProvider_WriteRead(testName, store);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task PersistenceProvider_FileStore_WriteRead()
        {
            string testName = TestContext.TestName;

            IStorageProvider store = new OrleansFileStorage();
            providerCfgProps.Add("RootDirectory", "Data");
            var cfg = new ProviderConfiguration(providerCfgProps, null);
            await store.Init(testName, storageProviderManager, cfg);

            await Test_PersistenceProvider_WriteRead(testName, store);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task PersistenceProvider_Sharded_WriteRead()
        {
            string testName = TestContext.TestName;

            IStorageProvider store1 = new MockStorageProvider(2);
            IStorageProvider store2 = new MockStorageProvider(2);
            await storageProviderManager.AddAndInitProvider("Store1", store1);
            await storageProviderManager.AddAndInitProvider("Store2", store2);
            var composite = await ConfigureShardedStorageProvider(testName, storageProviderManager);

            await Test_PersistenceProvider_WriteRead(testName, composite);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task PersistenceProvider_Sharded_9_WriteRead()
        {
            string testName = TestContext.TestName;

            IStorageProvider store1 = new MockStorageProvider(2);
            IStorageProvider store2 = new MockStorageProvider(2);
            IStorageProvider store3 = new MockStorageProvider(2);
            IStorageProvider store4 = new MockStorageProvider(2);
            IStorageProvider store5 = new MockStorageProvider(2);
            IStorageProvider store6 = new MockStorageProvider(2);
            IStorageProvider store7 = new MockStorageProvider(2);
            IStorageProvider store8 = new MockStorageProvider(2);
            IStorageProvider store9 = new MockStorageProvider(2);
            await storageProviderManager.AddAndInitProvider("Store1", store1);
            await storageProviderManager.AddAndInitProvider("Store2", store2);
            await storageProviderManager.AddAndInitProvider("Store3", store3);
            await storageProviderManager.AddAndInitProvider("Store4", store4);
            await storageProviderManager.AddAndInitProvider("Store5", store5);
            await storageProviderManager.AddAndInitProvider("Store6", store6);
            await storageProviderManager.AddAndInitProvider("Store7", store7);
            await storageProviderManager.AddAndInitProvider("Store8", store8);
            await storageProviderManager.AddAndInitProvider("Store9", store9);

            ShardedStorageProvider composite = await ConfigureShardedStorageProvider(testName, storageProviderManager);

            await Test_PersistenceProvider_WriteRead(testName, composite);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task PersistenceProvider_Azure_Read()
        {
            string testName = TestContext.TestName;

            IStorageProvider store = new AzureTableStorage();
            providerCfgProps.Add("DataConnectionString", StorageTestConstants.DataConnectionString);
            var cfg = new ProviderConfiguration(providerCfgProps, null);
            await store.Init(testName, storageProviderManager, cfg);

            await Test_PersistenceProvider_Read(testName, store);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task PersistenceProvider_Azure_WriteRead()
        {
            string testName = TestContext.TestName;

            IStorageProvider store = new AzureTableStorage();
            providerCfgProps.Add("DataConnectionString", StorageTestConstants.DataConnectionString);
            var cfg = new ProviderConfiguration(providerCfgProps, null);
            await store.Init(testName, storageProviderManager, cfg);

            await Test_PersistenceProvider_WriteRead(testName, store);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task PersistenceProvider_Azure_WriteRead_Json()
        {
            string testName = TestContext.TestName;

            IStorageProvider store = new AzureTableStorage();
            providerCfgProps.Add("DataConnectionString", StorageTestConstants.DataConnectionString);
            providerCfgProps.Add("UseJsonFormat", "true");
            var cfg = new ProviderConfiguration(providerCfgProps, null);
            await store.Init(testName, storageProviderManager, cfg);

            await Test_PersistenceProvider_WriteRead(testName, store);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public void AzureTableStorage_ConvertToFromStorageFormat()
        {
            TestStoreGrainState initialState = new TestStoreGrainState { A = "1", B = 2, C = 3 };
            AzureTableStorage.GrainStateEntity entity = new AzureTableStorage.GrainStateEntity();
            var storage = new AzureTableStorage();
            var logger = TraceLogger.GetLogger("PersistenceProviderTests");
            storage.InitLogger(logger);
            storage.ConvertToStorageFormat(initialState, entity);
            Assert.IsNotNull(entity.Data, "Entity.Data");
            var convertedState = (TestStoreGrainState)storage.ConvertFromStorageFormat(entity);
            Assert.IsNotNull(convertedState, "Converted state");
            Assert.AreEqual(initialState.A, convertedState.A, "A");
            Assert.AreEqual(initialState.B, convertedState.B, "B");
            Assert.AreEqual(initialState.C, convertedState.C, "C");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public async Task PersistenceProvider_Memory_FixedLatency_WriteRead()
        {
            string testName = TestContext.TestName;
            TimeSpan expectedLatency = TimeSpan.FromMilliseconds(200);

            IStorageProvider store = new MemoryStorageWithLatency();
            providerCfgProps.Add("Latency", expectedLatency.ToString());
            providerCfgProps.Add("MockCalls", "true");
            var cfg = new ProviderConfiguration(providerCfgProps, null);
            await store.Init(testName, storageProviderManager, cfg);

            GrainReference reference = GrainReference.FromGrainId(GrainId.NewId());
            var state = TestStoreGrainState.NewRandomState();
            Stopwatch sw = new Stopwatch();
            sw.Start();
            await store.WriteStateAsync(testName, reference, state);
            TimeSpan writeTime = sw.Elapsed;
            Console.WriteLine("{0} - Write time = {1}", store.GetType().FullName, writeTime);
            Assert.IsTrue(writeTime >= expectedLatency, "Write: Expected minimum latency = {0} Actual = {1}", expectedLatency, writeTime);

            sw.Restart();
            var storedState = new GrainState<TestStoreGrainState>();
            await store.ReadStateAsync(testName, reference, storedState);
            TimeSpan readTime = sw.Elapsed;
            Console.WriteLine("{0} - Read time = {1}", store.GetType().FullName, readTime);
            Assert.IsTrue(readTime >= expectedLatency, "Read: Expected minimum latency = {0} Actual = {1}", expectedLatency, readTime);
        }

        [TestMethod, TestCategory("Persistence"), TestCategory("Performance"), TestCategory("JSON")]
        public void Json_Perf_Newtonsoft_vs_Net()
        {
            int numIterations = 10000;

            Dictionary<string, object> dataValues = new Dictionary<string, object>();
            var dotnetJsonSerializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            string jsonData = null;
            int[] idx = { 0 };
            TimeSpan baseline = UnitTestSiloHost.TimeRun(numIterations, TimeSpan.Zero, ".Net JavaScriptSerializer",
            () =>
            {
                dataValues.Clear();
                dataValues.Add("A", idx[0]++);
                dataValues.Add("B", idx[0]++);
                dataValues.Add("C", idx[0]++);
                jsonData = dotnetJsonSerializer.Serialize(dataValues);
            });
            idx[0] = 0;
            TimeSpan elapsed = UnitTestSiloHost.TimeRun(numIterations, baseline, "Newtonsoft Json JavaScriptSerializer",
            () =>
            {
                dataValues.Clear();
                dataValues.Add("A", idx[0]++);
                dataValues.Add("B", idx[0]++);
                dataValues.Add("C", idx[0]++);
                jsonData = Newtonsoft.Json.JsonConvert.SerializeObject(dataValues);
            });
            Console.WriteLine("Elapsed: {0} Date: {1}", elapsed, jsonData);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence")]
        public void LoadClassByName()
        {
            string className = typeof(MockStorageProvider).FullName;
            Type classType = TypeUtils.ResolveType(className);
            Assert.IsNotNull(classType, "Type");
            Assert.IsTrue(typeof(IStorageProvider).IsAssignableFrom(classType), "Is an IStorageProvider : {0}", classType.FullName);
        }

        #region Utility functions

        private static async Task Test_PersistenceProvider_Read(string grainTypeName, IStorageProvider store)
        {
            GrainReference reference = GrainReference.FromGrainId(GrainId.NewId());
            TestStoreGrainState state = new TestStoreGrainState();
            Stopwatch sw = new Stopwatch();
            var storedGrainState = new GrainState<TestStoreGrainState>
            {
                State = new TestStoreGrainState()
            };
            sw.Start();
            await store.ReadStateAsync(grainTypeName, reference, storedGrainState);
            TimeSpan readTime = sw.Elapsed;
            Console.WriteLine("{0} - Read time = {1}", store.GetType().FullName, readTime);
            var storedState = storedGrainState.State;
            Assert.AreEqual(state.A, storedState.A, "A");
            Assert.AreEqual(state.B, storedState.B, "B");
            Assert.AreEqual(state.C, storedState.C, "C");
        }

        private static async Task Test_PersistenceProvider_WriteRead(string grainTypeName, IStorageProvider store)
        {
            GrainReference reference = GrainReference.FromGrainId(GrainId.NewId());
            var state = TestStoreGrainState.NewRandomState();
            Stopwatch sw = new Stopwatch();
            sw.Start();
            await store.WriteStateAsync(grainTypeName, reference, state);
            TimeSpan writeTime = sw.Elapsed;
            sw.Restart();
            var storedGrainState = new GrainState<TestStoreGrainState>
            {
                State = new TestStoreGrainState()
            };
            await store.ReadStateAsync(grainTypeName, reference, storedGrainState);
            TimeSpan readTime = sw.Elapsed;
            Console.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
            var storedState = storedGrainState.State;
            Assert.AreEqual(state.State.A, storedState.A, "A");
            Assert.AreEqual(state.State.B, storedState.B, "B");
            Assert.AreEqual(state.State.C, storedState.C, "C");
        }

        private async Task<ShardedStorageProvider> ConfigureShardedStorageProvider(string name, StorageProviderManager storageProviderMgr)
        {
            var composite = new ShardedStorageProvider();
            var provider1 = (IStorageProvider)storageProviderMgr.GetProvider("Store1");
            var provider2 = (IStorageProvider)storageProviderMgr.GetProvider("Store2");
            List<IProvider> providers = new List<IProvider>();
            providers.Add(provider1);
            providers.Add(provider2);
            var cfg = new ProviderConfiguration(providerCfgProps, providers);
            await composite.Init(name, storageProviderMgr, cfg);
            return composite;
        }
        #endregion
    }
}
