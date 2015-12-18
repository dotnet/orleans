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
            Stopwatch sw = new Stopwatch();
            sw.Start();
            var keys = GetKeys(name, reference);
            TestStoreGrainState storedState = new TestStoreGrainState();
            var data = store.ReadRow(keys);
            storedState.SetAll(data);
            TimeSpan readTime = sw.Elapsed;
            Console.WriteLine("{0} - Read time = {1}", store.GetType().FullName, readTime);
            Assert.AreEqual(state.A, storedState.A, "A");
            Assert.AreEqual(state.B, storedState.B, "B");
            Assert.AreEqual(state.C, storedState.C, "C");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public void Store_WriteRead()
        {
            string name = TestContext.TestName;

            ILocalDataStore store = new HierarchicalKeyStore(2);

            GrainReference reference = GrainReference.FromGrainId(GrainId.NewId());
            TestStoreGrainState state = TestStoreGrainState.NewRandomState();
            Stopwatch sw = new Stopwatch();
            sw.Start();
            var keys = GetKeys(name, reference);
            store.WriteRow(keys, state.AsDictionary(), state.Etag);
            TimeSpan writeTime = sw.Elapsed;
            sw.Restart();
            TestStoreGrainState storedState = new TestStoreGrainState();
            var data = store.ReadRow(keys);
            storedState.SetAll(data);
            TimeSpan readTime = sw.Elapsed;
            Console.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
            Assert.AreEqual(state.A, storedState.A, "A");
            Assert.AreEqual(state.B, storedState.B, "B");
            Assert.AreEqual(state.C, storedState.C, "C");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public void Store_Delete()
        {
            string name = TestContext.TestName;

            ILocalDataStore store = new HierarchicalKeyStore(2);

            GrainReference reference = GrainReference.FromGrainId(GrainId.NewId());
            IDictionary<string, object> data = TestStoreGrainState.NewRandomState().AsDictionary();

            Console.WriteLine("Using store = {0}", store.GetType().FullName);
            Stopwatch sw = new Stopwatch();

            var keys = GetKeys(name, reference);

            sw.Restart();
            string eTag = store.WriteRow(keys, data, null);
            Console.WriteLine("Write returned Etag={0} after {1} {2}", eTag, sw.Elapsed, StorageProviderUtils.PrintOneWrite(keys, data, eTag));

            sw.Restart();
            data = store.ReadRow(keys);
            Console.WriteLine("Read returned {0} after {1}", StorageProviderUtils.PrintOneWrite(keys, data, eTag), sw.Elapsed);
            Assert.IsNotNull(data, "Should get some data from Read");

            sw.Restart();
            bool ok = store.DeleteRow(keys, eTag);
            Assert.IsTrue(ok, "Row deleted OK after {0}. Etag={1} Keys={2}", sw.Elapsed, eTag, StorageProviderUtils.PrintKeys(keys));

            sw.Restart();
            data = store.ReadRow(keys); // Try to re-read after delete
            Console.WriteLine("Re-Read took {0} and returned {1}", sw.Elapsed, StorageProviderUtils.PrintData(data));
            Assert.IsNotNull(data, "Should not get null data from Re-Read");
            Assert.IsTrue(data.Count == 0, "Should get no data from Re-Read but got: {0}", StorageProviderUtils.PrintData(data));

            sw.Restart();
            const string oldEtag = null;
            eTag = store.WriteRow(keys, data, oldEtag);
            Console.WriteLine("Write for Keys={0} Etag={1} Data={2} returned New Etag={3} after {4}", 
                StorageProviderUtils.PrintKeys(keys), oldEtag, StorageProviderUtils.PrintData(data), 
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
            TestStoreGrainState state = TestStoreGrainState.NewRandomState();
            state.A = name;
            store.WriteRow(keys, state.AsDictionary(), state.Etag);

            // Write #2
            keys = new[]
            {
                Tuple.Create("GrainType", name),
                Tuple.Create("GrainId", "2")
            }.ToList();
            state = TestStoreGrainState.NewRandomState();
            state.A = name;
            store.WriteRow(keys, state.AsDictionary(), state.Etag);

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
            TestStoreGrainState state = TestStoreGrainState.NewRandomState();
            Stopwatch sw = new Stopwatch();
            sw.Start();
            IList<Tuple<string, string>> keys = new[]
            {
                Tuple.Create("GrainType", name),
                Tuple.Create("GrainId", reference.GrainId.GetPrimaryKey().ToString("N"))
            }.ToList();
            store.WriteRow(keys, state.AsDictionary(), state.Etag);
            TimeSpan writeTime = sw.Elapsed;
            sw.Restart();
            TestStoreGrainState storedState = new TestStoreGrainState();
            var data = store.ReadRow(keys);
            storedState.SetAll(data);
            TimeSpan readTime = sw.Elapsed;
            Console.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
            Assert.AreEqual(state.A, storedState.A, "A");
            Assert.AreEqual(state.B, storedState.B, "B");
            Assert.AreEqual(state.C, storedState.C, "C");
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
    }

    #region Grain State class for these tests
    public class TestStoreGrainState : GrainState
    {
        public string A { get; set; }
        public int B { get; set; }
        public long C { get; set; }

        public TestStoreGrainState()
            : base("System.Object")
        {
        }

        public static TestStoreGrainState NewRandomState()
        {
            return new TestStoreGrainState
            {
                A = TestConstants.random.Next().ToString(CultureInfo.InvariantCulture),
                B = TestConstants.random.Next(),
                C = TestConstants.random.Next()
            };
        }

        public override IDictionary<string, object> AsDictionary()
        {
            Dictionary<string, object> values = new Dictionary<string, object>();
            values["A"] = this.A;
            values["B"] = this.B;
            values["C"] = this.C;
            return values;
        }

        public override void SetAll(IDictionary<string, object> values)
        {
            object value;
            if (values.TryGetValue("A", out value)) A = (string) value;
            //if (values.TryGetValue("B", out value)) B = (int)value;
            if (values.TryGetValue("B", out value)) B = value is Int64 ? (int) (long) value : (int) value;
            if (values.TryGetValue("C", out value)) { C = value is Int32 ? (int) value : (long) value; }
        }
    }
    #endregion
}
