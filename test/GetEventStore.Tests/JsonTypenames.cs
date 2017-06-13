using EventSourcing.Tests;
using Orleans.EventSourcing;
using Orleans.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace GetEventStore.Tests
{
    [Collection(TestEnvironmentFixture.DefaultCollection)]
    [TestCategory("Functional")]
    public class JsonTypenames : OrleansTestingBase, IClassFixture<ProvidersFixture>
    {

        private readonly ITestOutputHelper output;
        private readonly ProvidersFixture fixture;

        public JsonTypenames(ITestOutputHelper output, ProvidersFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
        }

        private Task Store<E>(string streamName, E obj, bool storeAllTypenames = false)
        {
            var provider = storeAllTypenames ? fixture.EventStoreAllTypenames : fixture.EventStoreDefault;
            using (var stream = provider.GetEventStreamHandle<E>(streamName))
            {
                return stream.Append(new KeyValuePair<Guid, E>[] { new KeyValuePair<Guid, E>(Guid.NewGuid(), obj) });
            }
        }

        private async Task<E> Load<E>(string streamName)
        {
            using (var stream = fixture.EventStoreDefault.GetEventStreamHandle<E>(streamName))
            {
                var rsp = await stream.Load(0, 1);
                return rsp.Events[0].Value;
            }
        }

        class F1 { public int A = 0; }
        class F2 { public int A = 0; public int B = 0; }
        class F3 { public int A = 0; public int B = 0; public int C = 0; }

        [Fact]
        public async Task StoreRuntimeType()
        {
            var streamName = Guid.NewGuid().ToString();

            // typename is stored because runtime type <F2> is not identical to static type <object>
            await Store<object>(streamName, new F2() { A = 5, B = 6 });

            // therefore deserialization into <object> produces same runtime type 
            var f2 = (F2)await Load<object>(streamName);
            Assert.Equal(5, f2.A);
            Assert.Equal(6, f2.B);
        }

        [Fact]
        public async Task DeserializationError()
        {
            var streamName = Guid.NewGuid().ToString();

            // typename is stored because runtime type <F2> is not identical to static type <object>
            await Store<object>(streamName, new F2() { A = 5, B = 6 });

            // therefore deserialization into incompatible type <F3> causes error
            var e = await Assert.ThrowsAsync<Newtonsoft.Json.JsonSerializationException>(async () =>
            {
                var f3 = await Load<F3>(streamName);
            });
        }

        [Fact]
        public async Task AlwaysStoreRuntimeType()
        {
            var streamName = Guid.NewGuid().ToString();

            // for this provider config, typename is always stored, 
            // even though runtime type matches static type
            await Store<F2>(streamName, new F2() { A = 5, B = 6 }, true);

            // therefore deserialization into incompatible type causes error
            var e = await Assert.ThrowsAsync<Newtonsoft.Json.JsonSerializationException>(async () =>
            {
                var f3 = await Load<F3>(streamName);
            });
        }

        [Fact]
        public async Task StructuralSubtyping()
        {
            var streamName = Guid.NewGuid().ToString();
            // typename is not stored because runtime type matches static type
            await Store<F2>(streamName, new F2() { A = 5, B = 6 });

            // therefore we can deserialize into a different, larger class
            var f3 = await Load<F3>(streamName);
            Assert.Equal(5, f3.A);
            Assert.Equal(6, f3.B);
            Assert.Equal(0, f3.C);

            // or a different, smaller class
            var f1 = await Load<F1>(streamName);
            Assert.Equal(5, f1.A);
        }

        class A1 { public int[] A; }
        class A2 { public List<int> A; }

        [Fact]
        public async Task ArrayToList()
        {
            var streamName = Guid.NewGuid().ToString();
            // typename is not stored because runtime type matches static type
            await Store<A1>(streamName, new A1() { A = new int[] { 1, 2 } });

            // therefore we can deserialize into list instead of array
            var a2 = await Load<A2>(streamName);
            Assert.Equal(1, a2.A[0]);
            Assert.Equal(2, a2.A[1]);
        }

        [Fact]
        public async Task ListToArray()
        {
            var streamName = Guid.NewGuid().ToString();
            // typename is not stored because runtime type matches static type
            await Store<A2>(streamName, new A2() { A = (new int[] { 1, 2 }).ToList() });

            // therefore we can deserialize into array instead of list
            var a1 = await Load<A1>(streamName);
            Assert.Equal(1, a1.A[0]);
            Assert.Equal(2, a1.A[1]);
        }

    }
}
