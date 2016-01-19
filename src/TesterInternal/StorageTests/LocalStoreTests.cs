using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Storage;

namespace UnitTests.StorageTests
{
    [Serializable]
    public enum ProviderType
    {
        AzureTable,
        Memory,
        Mock,
        File,
        Sql
    }

    [TestClass]
    [DeploymentItem("ClientConfigurationForTesting.xml")]
    public class LocalStoreTests
    {
        public TestContext TestContext { get; set; }

        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            Console.WriteLine("ClassInitialize {0}", testContext.TestName);

            BufferPool.InitGlobalBufferPool(new MessagingConfiguration(false));

            ClientConfiguration cfg = ClientConfiguration.LoadFromFile("ClientConfigurationForTesting.xml");
            TraceLogger.Initialize(cfg);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            LocalDataStoreInstance.LocalDataStore = null;
        }

        [TestInitialize]
        public void TestInitialize()
        {
            LocalDataStoreInstance.LocalDataStore = null;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            Console.WriteLine("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
            LocalDataStoreInstance.LocalDataStore = null;
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public void Store_Read()
        {
            string name = TestContext.TestName;

            ILocalDataStore store = new HierarchicalKeyStore(2);

            GrainReference reference = GrainReference.FromGrainId(GrainId.NewId());
            TestStoreGrainState state = new TestStoreGrainState();
            var stateProperties = AsDictionary(state);
            var keys = GetKeys(name, reference);
            store.WriteRow(keys, stateProperties, null);
            Stopwatch sw = new Stopwatch();
            sw.Start();
            var data = store.ReadRow(keys);
            TimeSpan readTime = sw.Elapsed;
            Console.WriteLine("{0} - Read time = {1}", store.GetType().FullName, readTime);
            Assert.AreEqual(state.A, data["A"], "A");
            Assert.AreEqual(state.B, data["B"], "B");
            Assert.AreEqual(state.C, data["C"], "C");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public void Store_WriteRead()
        {
            string name = TestContext.TestName;

            ILocalDataStore store = new HierarchicalKeyStore(2);

            GrainReference reference = GrainReference.FromGrainId(GrainId.NewId());
            var state = TestStoreGrainState.NewRandomState();
            Stopwatch sw = new Stopwatch();
            sw.Start();
            var keys = GetKeys(name, reference);
            var stateProperties = AsDictionary(state.State);
            store.WriteRow(keys, stateProperties, state.ETag);
            TimeSpan writeTime = sw.Elapsed;
            sw.Restart();
            var data = store.ReadRow(keys);
            TimeSpan readTime = sw.Elapsed;
            Console.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
            Assert.AreEqual(state.State.A, data["A"], "A");
            Assert.AreEqual(state.State.B, data["B"], "B");
            Assert.AreEqual(state.State.C, data["C"], "C");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public void Store_Delete()
        {
            string name = TestContext.TestName;

            ILocalDataStore store = new HierarchicalKeyStore(2);

            GrainReference reference = GrainReference.FromGrainId(GrainId.NewId());
            var data = TestStoreGrainState.NewRandomState();

            Console.WriteLine("Using store = {0}", store.GetType().FullName);
            Stopwatch sw = new Stopwatch();

            var keys = GetKeys(name, reference);

            sw.Restart();
            string eTag = store.WriteRow(keys, AsDictionary(data.State), null);
            Console.WriteLine("Write returned Etag={0} after {1} {2}", eTag, sw.Elapsed, StorageProviderUtils.PrintOneWrite(keys, data, eTag));

            sw.Restart();
            var storedData = store.ReadRow(keys);
            Console.WriteLine("Read returned {0} after {1}", StorageProviderUtils.PrintOneWrite(keys, storedData, eTag), sw.Elapsed);
            Assert.IsNotNull(data, "Should get some data from Read");

            sw.Restart();
            bool ok = store.DeleteRow(keys, eTag);
            Assert.IsTrue(ok, "Row deleted OK after {0}. Etag={1} Keys={2}", sw.Elapsed, eTag, StorageProviderUtils.PrintKeys(keys));

            sw.Restart();
            storedData = store.ReadRow(keys); // Try to re-read after delete
            Console.WriteLine("Re-Read took {0} and returned {1}", sw.Elapsed, StorageProviderUtils.PrintData(storedData));
            Assert.IsNotNull(data, "Should not get null data from Re-Read");
            Assert.IsTrue(storedData.Count == 0, "Should get no data from Re-Read but got: {0}", StorageProviderUtils.PrintData(storedData));

            sw.Restart();
            const string oldEtag = null;
            eTag = store.WriteRow(keys, storedData, oldEtag);
            Console.WriteLine("Write for Keys={0} Etag={1} Data={2} returned New Etag={3} after {4}",
                StorageProviderUtils.PrintKeys(keys), oldEtag, StorageProviderUtils.PrintData(storedData),
                eTag, sw.Elapsed);

            sw.Restart();
            ok = store.DeleteRow(keys, eTag);
            Assert.IsTrue(ok, "Row deleted OK after {0}. Etag={1} Keys={2}", sw.Elapsed, eTag, StorageProviderUtils.PrintKeys(keys));
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public void Store_ReadMulti()
        {
            string name = TestContext.TestName;

            ILocalDataStore store = new HierarchicalKeyStore(2);

            // Write #1
            IList<Tuple<string, string>> keys = new[]
            {
                Tuple.Create("GrainType", name),
                Tuple.Create("GrainId", "1")
            }.ToList();
            var grainState = TestStoreGrainState.NewRandomState();
            var state = grainState.State;
            state.A = name;
            store.WriteRow(keys, AsDictionary(state), grainState.ETag);

            // Write #2
            keys = new[]
            {
                Tuple.Create("GrainType", name),
                Tuple.Create("GrainId", "2")
            }.ToList();
            grainState = TestStoreGrainState.NewRandomState();
            state = grainState.State;
            state.A = name;
            store.WriteRow(keys, AsDictionary(state), grainState.ETag);

            // Multi Read
            keys = new[]
            {
                Tuple.Create("GrainType", name)
            }.ToList();

            var results = store.ReadMultiRow(keys);

            Assert.AreEqual(2, results.Count, "Count");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public void GrainState_Store_WriteRead()
        {
            string name = TestContext.TestName;

            ILocalDataStore store = new HierarchicalKeyStore(2);

            GrainReference reference = GrainReference.FromGrainId(GrainId.NewId());
            var grainState = TestStoreGrainState.NewRandomState();
            var state = grainState.State;
            Stopwatch sw = new Stopwatch();
            sw.Start();
            IList<Tuple<string, string>> keys = new[]
            {
                Tuple.Create("GrainType", name),
                Tuple.Create("GrainId", reference.GrainId.GetPrimaryKey().ToString("N"))
            }.ToList();
            store.WriteRow(keys, AsDictionary(state), grainState.ETag);
            TimeSpan writeTime = sw.Elapsed;
            sw.Restart();
            var data = store.ReadRow(keys);
            TimeSpan readTime = sw.Elapsed;
            Console.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
            Assert.AreEqual(state.A, data["A"], "A");
            Assert.AreEqual(state.B, data["B"], "B");
            Assert.AreEqual(state.C, data["C"], "C");
        }

        // ---------- Utility methods ----------

        private static IList<Tuple<string, string>> GetKeys(string grainTypeName, GrainReference grain)
        {
            var keys = new[]
            {
                Tuple.Create("GrainType", grainTypeName),
                Tuple.Create("GrainId", grain.GrainId.GetPrimaryKey().ToString("N"))
            };
            return keys.ToList();
        }

        private static Dictionary<string, object> AsDictionary(object state)
        {
            return state.GetType().GetProperties()
                .Select(v => new KeyValuePair<string, object>(v.Name, v.GetValue(state)))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }
    }

    #region Grain State class for these tests
    [Serializable]
    public class TestStoreGrainState
    {
        public string A { get; set; }
        public int B { get; set; }
        public long C { get; set; }

        internal static GrainState<TestStoreGrainState> NewRandomState()
        {
            return new GrainState<TestStoreGrainState>
            {
                State = new TestStoreGrainState
                {
                    A = TestConstants.random.Next().ToString(CultureInfo.InvariantCulture),
                    B = TestConstants.random.Next(),
                    C = TestConstants.random.Next()
                }
            };
        }
    }
    #endregion
}
