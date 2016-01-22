using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Storage;

namespace UnitTests.StorageTests
{
    [TestClass]
    [DeploymentItem("OrleansConfiguration.xml")]
    [DeploymentItem("ClientConfiguration.xml")]
    public class HierarchicalKeyStoreTests
    {
        public TestContext TestContext { get; set; }

        private const string KeyName1 = "Key1";
        private const string KeyName2 = "Key2";
        private const string KeyName3 = "Key3";
        private const string ValueName1 = "Value1";
        private const string ValueName2 = "Value2";
        private const string ValueName3 = "Value3";

        private static int _keyCounter = 1;

        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            Console.WriteLine("ClassInitialize {0}", testContext.TestName);
            testContext.WriteLine("ClassInitialize");
            BufferPool.InitGlobalBufferPool(new MessagingConfiguration(false));
            ClientConfiguration cfg = ClientConfiguration.StandardLoad();
            testContext.WriteLine(cfg.ToString());
            TraceLogger.Initialize(cfg);
            LocalDataStoreInstance.LocalDataStore = null;
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            LocalDataStoreInstance.LocalDataStore = null;
        }

        [TestMethod, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void Comparer_InsideRange()
        {
            string testName = TestContext.TestName;

            string rangeParamName = "Column1";
            string fromValue = "Rem10";
            string toValue = "Rem11";

            var compareClause = MemoryStorage.GetComparer(rangeParamName, fromValue, toValue);

            var data = new Dictionary<string, object>();

            data[rangeParamName] = "Rem09";
            Assert.IsFalse(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);

            data[rangeParamName] = "Rem10";
            Assert.IsTrue(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);

            data[rangeParamName] = "Rem11";
            Assert.IsTrue(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);

            data[rangeParamName] = "Rem12";
            Assert.IsFalse(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);

            data[rangeParamName] = testName;
            Assert.IsFalse(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void Comparer_OutsideRange()
        {
            string testName = TestContext.TestName;

            string rangeParamName = "Column1";
            string toValue = "Rem10";
            string fromValue = "Rem12";

            var compareClause = MemoryStorage.GetComparer(rangeParamName, fromValue, toValue);

            var data = new Dictionary<string, object>();

            data[rangeParamName] = "Rem09";
            Assert.IsTrue(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);

            data[rangeParamName] = "Rem10";
            Assert.IsTrue(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);

            data[rangeParamName] = "Rem11";
            Assert.IsFalse(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);

            data[rangeParamName] = "Rem12";
            Assert.IsTrue(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);

            data[rangeParamName] = testName;
            Assert.IsTrue(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void Comparer_SameRange()
        {
            string testName = TestContext.TestName;

            string rangeParamName = "Column1";
            string fromValue = "Rem11";
            string toValue = "Rem11";

            var compareClause = MemoryStorage.GetComparer(rangeParamName, fromValue, toValue);

            var data = new Dictionary<string, object>();

            data[rangeParamName] = "Rem09";
            Assert.IsFalse(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);

            data[rangeParamName] = "Rem10";
            Assert.IsFalse(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);

            data[rangeParamName] = "Rem11";
            Assert.IsTrue(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);

            data[rangeParamName] = "Rem12";
            Assert.IsFalse(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);

            data[rangeParamName] = testName;
            Assert.IsFalse(compareClause(data), "From={0} To={1} Compare Value={2}", fromValue, toValue, data[rangeParamName]);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_MakeKey()
        {
            string testName = TestContext.TestName;

            var keys = new[]
            {
                Tuple.Create("One", "1"),
                Tuple.Create("Two", "2")
            }.ToList();

            string keyStr = HierarchicalKeyStore.MakeStoreKey(keys);
            Assert.AreEqual("One=1+Two=2", keyStr, "Output from MakeStoreKey");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_1Key_Read_Write()
        {
            string testName = TestContext.TestName;

            int key1 = _keyCounter++;

            var keys = new[]
            {
                Tuple.Create(KeyName1, key1.ToString(CultureInfo.InvariantCulture))
            }.ToList();

            var data = new Dictionary<string, object>();
            data.Add(ValueName1, testName);

            var store = new HierarchicalKeyStore(1);

            string eTag = store.WriteRow(keys, data, null);

            var result = store.ReadRow(keys);

            Assert.IsNotNull(result, "Null result");
            foreach (string valueName in data.Keys)
            {
                Assert.AreEqual(data[valueName], result[valueName], valueName);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_2Key_Read_Write()
        {
            string testName = TestContext.TestName;

            int key1 = _keyCounter++;
            int key2 = _keyCounter++;

            var keys = new[]
            {
                Tuple.Create(KeyName1, key1.ToString(CultureInfo.InvariantCulture)),
                Tuple.Create(KeyName2, key2.ToString(CultureInfo.InvariantCulture))
            }.ToList();

            var data = new Dictionary<string, object>();
            data.Add(ValueName1, testName);

            var store = new HierarchicalKeyStore(2);

            string eTag = store.WriteRow(keys, data, null);

            var result = store.ReadRow(keys);

            Assert.IsNotNull(result, "Null result");
            foreach (string valueName in data.Keys)
            {
                Assert.AreEqual(data[valueName], result[valueName], valueName);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_3Key_Read_Write()
        {
            string testName = TestContext.TestName;

            int key1 = _keyCounter++;
            int key2 = _keyCounter++;
            int key3 = _keyCounter++;

            var keys = new[]
            {
                Tuple.Create(KeyName1, key1.ToString(CultureInfo.InvariantCulture)),
                Tuple.Create(KeyName2, key2.ToString(CultureInfo.InvariantCulture)),
                Tuple.Create(KeyName3, key3.ToString(CultureInfo.InvariantCulture))
            }.ToList();

            var data = new Dictionary<string, object>();
            data[ValueName1] = testName + 1;
            data[ValueName2] = testName + 2;
            data[ValueName3] = testName + 3;

            var store = new HierarchicalKeyStore(3);

            string eTag = store.WriteRow(keys, data, null);

            var result = store.ReadRow(keys);

            Assert.IsNotNull(result, "Null result");
            foreach (string valueName in data.Keys)
            {
                Assert.AreEqual(data[valueName], result[valueName], valueName);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_Write2()
        {
            string testName = TestContext.TestName;

            int key1 = _keyCounter++;
            int key2 = _keyCounter++;

            List<Tuple<string, string>> keys = MakeKeys(key1, key2);

            var data = new Dictionary<string, object>();
            data[ValueName1] = testName;

            var store = new HierarchicalKeyStore(keys.Count);

            // Write #1
            string eTag = store.WriteRow(keys, data, null);

            data[ValueName1] = "One";
            data[ValueName2] = "Two";
            data[ValueName3] = "Three";

            // Write #2
            string newEtag = store.WriteRow(keys, data, eTag);

            var result = store.ReadRow(keys);

            Assert.IsNotNull(result, "Null result");
            foreach (string valueName in data.Keys)
            {
                Assert.AreEqual(data[valueName], result[valueName], valueName);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_DeleteRow()
        {
            string testName = TestContext.TestName;

            int key1 = _keyCounter++;
            int key2 = _keyCounter++;
            int key3 = _keyCounter++;
            int key4 = _keyCounter++;

            List<Tuple<string, string>> keys1 = MakeKeys(key1, key2);
            List<Tuple<string, string>> keys2 = MakeKeys(key3, key4);

            var data = new Dictionary<string, object>();
            data[ValueName1] = testName;

            var store = new HierarchicalKeyStore(keys1.Count);

            // Write #1
            string eTag = store.WriteRow(keys1, data, null);

            data[ValueName1] = "One";
            data[ValueName2] = "Two";
            data[ValueName3] = "Three";

            // Write #2
            string newEtag = store.WriteRow(keys2, data, eTag);

            store.DeleteRow(keys1, newEtag);

            var result = store.ReadRow(keys1);

            Assert.IsNotNull(result, "Should not be Null result after DeleteRow");
            Assert.AreEqual(0, result.Count, "No data after DeleteRow");

            result = store.ReadRow(keys2);

            Assert.IsNotNull(result, "Null result");
            foreach (string valueName in data.Keys)
            {
                Assert.AreEqual(data[valueName], result[valueName], valueName);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_Read_PartialKey()
        {
            string testName = TestContext.TestName;

            int key1 = _keyCounter++;
            int key2 = _keyCounter++;

            List<Tuple<string, string>> keys = MakeKeys(key1, key2);

            var data = new Dictionary<string, object>();
            data[ValueName1] = testName + 1;
            data[ValueName2] = testName + 2;
            data[ValueName3] = testName + 3;

            var store = new HierarchicalKeyStore(keys.Count);

            string eTag = store.WriteRow(keys, data, null);

            var readKeys = new List<Tuple<string, string>>();
            readKeys.Add(keys.First());

            var results = store.ReadMultiRow(readKeys);

            Assert.IsNotNull(results, "Null results");
            Assert.AreEqual(1, results.Count, "Number of results");

            var result = results.First();

            Assert.IsNotNull(result, "Null result");
            foreach (string valueName in data.Keys)
            {
                Assert.AreEqual(data[valueName], result[valueName], valueName);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_KeyNotFound()
        {
            string testName = TestContext.TestName;

            int key1 = _keyCounter++;
            int key2 = _keyCounter++;

            List<Tuple<string, string>> keys = MakeKeys(key1, key2);

            var store = new HierarchicalKeyStore(keys.Count);

            var result = store.ReadRow(keys);

            Assert.IsNotNull(result, "Null result");
            Assert.AreEqual(0, result.Count, "No data");
        }

        // Utility methods

        private List<Tuple<string, string>> MakeKeys(int key1, int key2)
        {
            var keys = new[]
            {
                Tuple.Create(KeyName1, key1.ToString(CultureInfo.InvariantCulture)),
                Tuple.Create(KeyName2, key2.ToString(CultureInfo.InvariantCulture))
            }.ToList();
            return keys;
        }
    }
}
