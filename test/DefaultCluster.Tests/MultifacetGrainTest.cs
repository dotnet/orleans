using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    //using ValueUpdateEventArgs = MultifacetGrainClient.ValueUpdateEventArgs;
    public class MultifacetGrainTest : HostedTestClusterEnsureDefaultStarted
    {
        private IMultifacetWriter writer;
        private IMultifacetReader reader;

        //int eventCounter;
        private const int EXPECTED_NUMBER_OF_EVENTS = 4;
        private readonly TimeSpan timeout = TimeSpan.FromSeconds(5);

        public MultifacetGrainTest(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("Functional"), TestCategory("Cast")]
        public void RWReferences()
        {
            writer = this.GrainFactory.GetGrain<IMultifacetWriter>(GetRandomGrainId());
            reader = writer.AsReference<IMultifacetReader>();
            
            int x = 1234;
            bool ok = writer.SetValue(x).Wait(timeout);
            if (!ok) throw new TimeoutException();
            int y = reader.GetValue().Result;
            Assert.Equal(x, y);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void RWReferencesInvalidCastException()
        {
            Assert.Throws<InvalidCastException>(() =>
            {
                reader = this.GrainFactory.GetGrain<IMultifacetReader>(GetRandomGrainId());
                writer = (IMultifacetWriter)reader;
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task MultifacetFactory()
        {
            IMultifacetFactoryTestGrain factory = this.GrainFactory.GetGrain<IMultifacetFactoryTestGrain>(GetRandomGrainId());
            IMultifacetTestGrain grain = this.GrainFactory.GetGrain<IMultifacetTestGrain>(GetRandomGrainId());
            IMultifacetWriter writer = await factory.GetWriter(grain /*"MultifacetFactory"*/);
            IMultifacetReader reader = await factory.GetReader(grain /*"MultifacetFactory"*/);
            writer.SetValue(5).Wait();
            int v = reader.GetValue().Result;
            Assert.Equal(5, v);
            
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task Multifacet_InterfacesAsArguments()
        {
            IMultifacetFactoryTestGrain factory = this.GrainFactory.GetGrain<IMultifacetFactoryTestGrain>(GetRandomGrainId());
            IMultifacetTestGrain grain = this.GrainFactory.GetGrain<IMultifacetTestGrain>(GetRandomGrainId());
            factory.SetReader(grain).Wait();
            factory.SetWriter(grain).Wait();
            IMultifacetWriter writer = await factory.GetWriter();
            IMultifacetReader reader = await factory.GetReader();
            writer.SetValue(10).Wait();
            int v = reader.GetValue().Result;
            Assert.Equal(10, v);
        }
    }
}
