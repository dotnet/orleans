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
    public class ObjectIdentity : OrleansTestingBase, IClassFixture<ProvidersFixture>
    {

        private readonly ITestOutputHelper output;
        private readonly ProvidersFixture fixture;

        public ObjectIdentity(ITestOutputHelper output, ProvidersFixture fixture)
        {
            this.output = output;
            this.fixture = fixture;
        }

        private Task Store<E>(string streamName, E obj, bool serializeObjectIdentity = false)
        {
            var provider = serializeObjectIdentity ? fixture.EventStoreObjectIdentity : fixture.EventStoreDefault;
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

        class N { public string val; public N left; public N right; }



        [Fact]
        public async Task StoreTree()
        {
            var streamName = Guid.NewGuid().ToString();
            var left = new N() { val = "l" };
            var right = new N() { val = "r" };

            await Store<N>(streamName, new N() { left = left, right = right });

            var n = await Load<N>(streamName);

            Assert.Equal("l", n.left.val);
            Assert.Equal("r", n.right.val);
        }


        [Fact]
        public async Task StoreDag()
        {
            var streamName = Guid.NewGuid().ToString();
            var child = new N() { val = "c" };

            await Store<N>(streamName, new N() { left = child, right = child });

            var n = await Load<N>(streamName);

            Assert.Equal("c", n.left.val);
            Assert.Equal("c", n.right.val);

            // shared node is not recognized because object identity was not stored
            Assert.NotEqual(n.left, n.right);
        }

        [Fact]
        public async Task StoreDagWithObjectIdentity()
        {
            var streamName = Guid.NewGuid().ToString();
            var child = new N() { val = "c" };

            await Store<N>(streamName, new N() { left = child, right = child }, true);

            var n = await Load<N>(streamName);

            Assert.Equal("c", n.left.val);
            Assert.Equal("c", n.right.val);

            // shared node is recognized because object identity was stored
            Assert.Equal(n.left, n.right);
        }
    }
}
