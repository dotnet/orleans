using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace UnitTests.StorageTests
{
    public class HierarchicalKeyStoreTests : IClassFixture<HierarchicalKeyStoreTests.Fixture>
    {
        public class Fixture
        {
            public Fixture()
            {
                BufferPool.InitGlobalBufferPool(new SiloMessagingOptions());
            }
        }

        private const string KeyName1 = "Key1";
        private const string KeyName2 = "Key2";
        private const string KeyName3 = "Key3";
        private const string ValueName1 = "Value1";
        private const string ValueName2 = "Value2";
        private const string ValueName3 = "Value3";

        private static int _keyCounter = 1;

        [Fact, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_MakeKey()
        {
            //string testName = TestContext.TestName;

            var keys = new[]
            {
                Tuple.Create("One", "1"),
                Tuple.Create("Two", "2")
            }.ToList();

            string keyStr = HierarchicalKeyStore.MakeStoreKey(keys);
            Assert.Equal("One=1+Two=2", keyStr); // Output from MakeStoreKey
        }

        [Fact, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_1Key_Read_Write()
        {
            string testName = Guid.NewGuid().ToString(); //TestContext.TestName;

            int key1 = _keyCounter++;

            var keys = new[]
            {
                Tuple.Create(KeyName1, key1.ToString(CultureInfo.InvariantCulture))
            }.ToList();

            var data = new Dictionary<string, object>();
            data.Add(ValueName1, testName);

            var store = new HierarchicalKeyStore(1);
            _ = store.WriteRow(keys, data, null);

            var result = store.ReadRow(keys);

            Assert.NotNull(result); // Null result
            foreach (string valueName in data.Keys)
            {
                Assert.Equal(data[valueName], result[valueName]);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_2Key_Read_Write()
        {
            string testName = Guid.NewGuid().ToString(); //TestContext.TestName;

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
            _ = store.WriteRow(keys, data, null);

            var result = store.ReadRow(keys);

            Assert.NotNull(result); // Null result
            foreach (string valueName in data.Keys)
            {
                Assert.Equal(data[valueName], result[valueName]);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_3Key_Read_Write()
        {
            string testName = Guid.NewGuid().ToString(); //TestContext.TestName;

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
            _ = store.WriteRow(keys, data, null);

            var result = store.ReadRow(keys);

            Assert.NotNull(result); // Null result
            foreach (string valueName in data.Keys)
            {
                Assert.Equal(data[valueName], result[valueName]);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_Write2()
        {
            string testName = Guid.NewGuid().ToString(); //TestContext.TestName;

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
            _ = store.WriteRow(keys, data, eTag);

            var result = store.ReadRow(keys);

            Assert.NotNull(result); // Null result
            foreach (string valueName in data.Keys)
            {
                Assert.Equal(data[valueName], result[valueName]);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_DeleteRow()
        {
            string testName = Guid.NewGuid().ToString(); //TestContext.TestName;

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

            Assert.NotNull(result); // Should not be Null result after DeleteRow
            Assert.Equal(0, result.Count); // No data after DeleteRow

            result = store.ReadRow(keys2);

            Assert.NotNull(result); // Null result
            foreach (string valueName in data.Keys)
            {
                Assert.Equal(data[valueName], result[valueName]);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_Read_PartialKey()
        {
            string testName = Guid.NewGuid().ToString(); //TestContext.TestName;

            int key1 = _keyCounter++;
            int key2 = _keyCounter++;

            List<Tuple<string, string>> keys = MakeKeys(key1, key2);

            var data = new Dictionary<string, object>();
            data[ValueName1] = testName + 1;
            data[ValueName2] = testName + 2;
            data[ValueName3] = testName + 3;

            var store = new HierarchicalKeyStore(keys.Count);
            _ = store.WriteRow(keys, data, null);

            var readKeys = new List<Tuple<string, string>>();
            readKeys.Add(keys.First());

            var results = store.ReadMultiRow(readKeys);

            Assert.NotNull(results); // Null results
            Assert.Equal(1, results.Count); // Number of results

            var result = results.First();

            Assert.NotNull(result); // Null result
            foreach (string valueName in data.Keys)
            {
                Assert.Equal(data[valueName], result[valueName]);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("MemoryStore")]
        public void HKS_KeyNotFound()
        {
            _ = Guid.NewGuid().ToString(); //TestContext.TestName;

            int key1 = _keyCounter++;
            int key2 = _keyCounter++;

            List<Tuple<string, string>> keys = MakeKeys(key1, key2);

            var store = new HierarchicalKeyStore(keys.Count);

            var result = store.ReadRow(keys);

            Assert.NotNull(result); // Null result
            Assert.Equal(0, result.Count); // No data
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
