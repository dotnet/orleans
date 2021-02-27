using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Orleans.Runtime;
using Orleans.Storage;
using TestExtensions;
using UnitTests.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.StorageTests
{
    [Serializable]
    public enum ProviderType
    {
        AzureTable,
        Memory,
        Mock,
        File,
        AdoNet
    }
    
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    public class LocalStoreTests
    {
        private readonly ITestOutputHelper output;
        private readonly TestEnvironmentFixture fixture;

        public LocalStoreTests(ITestOutputHelper output, TestEnvironmentFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public void Store_Read()
        {
            string name = Guid.NewGuid().ToString();//TestContext.TestName;

            ILocalDataStore store = new HierarchicalKeyStore(2);

            GrainReference reference = (GrainReference)this.fixture.InternalGrainFactory.GetGrain(LegacyGrainId.NewId());
            TestStoreGrainState state = new TestStoreGrainState();
            var stateProperties = AsDictionary(state);
            var keys = GetKeys(name, reference);
            store.WriteRow(keys, stateProperties, null);
            Stopwatch sw = new Stopwatch();
            sw.Start();
            var data = store.ReadRow(keys);
            TimeSpan readTime = sw.Elapsed;
            output.WriteLine("{0} - Read time = {1}", store.GetType().FullName, readTime);
            Assert.Equal(state.A,  data["A"]);  // "A"
            Assert.Equal(state.B,  data["B"]);  // "B"
            Assert.Equal(state.C,  data["C"]);  // "C"
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public void Store_WriteRead()
        {
            string name = Guid.NewGuid().ToString();//TestContext.TestName;

            ILocalDataStore store = new HierarchicalKeyStore(2);

            GrainReference reference = (GrainReference)fixture.InternalGrainFactory.GetGrain(LegacyGrainId.NewId());
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
            output.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
            Assert.Equal(state.State.A,  data["A"]);  // "A"
            Assert.Equal(state.State.B,  data["B"]);  // "B"
            Assert.Equal(state.State.C,  data["C"]);  // "C"
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public void Store_Delete()
        {
            string name = Guid.NewGuid().ToString();//TestContext.TestName;

            ILocalDataStore store = new HierarchicalKeyStore(2);

            GrainReference reference = (GrainReference)this.fixture.InternalGrainFactory.GetGrain(LegacyGrainId.NewId());
            var data = TestStoreGrainState.NewRandomState();

            output.WriteLine("Using store = {0}", store.GetType().FullName);
            Stopwatch sw = new Stopwatch();

            var keys = GetKeys(name, reference);

            sw.Restart();
            string eTag = store.WriteRow(keys, AsDictionary(data.State), null);
            output.WriteLine("Write returned Etag={0} after {1} {2}", eTag, sw.Elapsed, StorageProviderUtils.PrintOneWrite(keys, data, eTag));

            sw.Restart();
            var storedData = store.ReadRow(keys);
            output.WriteLine("Read returned {0} after {1}", StorageProviderUtils.PrintOneWrite(keys, storedData, eTag), sw.Elapsed);
            Assert.NotNull(data); // Should get some data from Read

            sw.Restart();
            bool ok = store.DeleteRow(keys, eTag);
            Assert.True(ok, $"Row deleted OK after {sw.Elapsed}. Etag={eTag} Keys={StorageProviderUtils.PrintKeys(keys)}");

            sw.Restart();
            storedData = store.ReadRow(keys); // Try to re-read after delete
            output.WriteLine("Re-Read took {0} and returned {1}", sw.Elapsed, StorageProviderUtils.PrintData(storedData));
            Assert.NotNull(data); // Should not get null data from Re-Read
            Assert.True(storedData.Count == 0, $"Should get no data from Re-Read but got: {StorageProviderUtils.PrintData(storedData)}");

            sw.Restart();
            const string oldEtag = null;
            eTag = store.WriteRow(keys, storedData, oldEtag);
            output.WriteLine("Write for Keys={0} Etag={1} Data={2} returned New Etag={3} after {4}",
                StorageProviderUtils.PrintKeys(keys), oldEtag, StorageProviderUtils.PrintData(storedData),
                eTag, sw.Elapsed);

            sw.Restart();
            ok = store.DeleteRow(keys, eTag);
            Assert.True(ok, $"Row deleted OK after {sw.Elapsed}. Etag={eTag} Keys={StorageProviderUtils.PrintKeys(keys)}");
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public void Store_ReadMulti()
        {
            string name = Guid.NewGuid().ToString();//TestContext.TestName;

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

            Assert.Equal(2,  results.Count);  // "Count"
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public void GrainState_Store_WriteRead()
        {
            string name = Guid.NewGuid().ToString();//TestContext.TestName;

            ILocalDataStore store = new HierarchicalKeyStore(2);

            GrainReference reference = (GrainReference)this.fixture.InternalGrainFactory.GetGrain(LegacyGrainId.NewId());
            var grainState = TestStoreGrainState.NewRandomState();
            var state = grainState.State;
            Stopwatch sw = new Stopwatch();
            sw.Start();
            IList<Tuple<string, string>> keys = new[]
            {
                Tuple.Create("GrainType", name),
                Tuple.Create("GrainId", reference.GrainId.ToString())
            }.ToList();
            store.WriteRow(keys, AsDictionary(state), grainState.ETag);
            TimeSpan writeTime = sw.Elapsed;
            sw.Restart();
            var data = store.ReadRow(keys);
            TimeSpan readTime = sw.Elapsed;
            output.WriteLine("{0} - Write time = {1} Read time = {2}", store.GetType().FullName, writeTime, readTime);
            Assert.Equal(state.A,  data["A"]);  // "A"
            Assert.Equal(state.B,  data["B"]);  // "B"
            Assert.Equal(state.C,  data["C"]);  // "C"
        }

        // ---------- Utility methods ----------

        private static IList<Tuple<string, string>> GetKeys(string grainTypeName, GrainReference grain)
        {
            var keys = new[]
            {
                Tuple.Create("GrainType", grainTypeName),
                Tuple.Create("GrainId", grain.GrainId.ToString())
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
}