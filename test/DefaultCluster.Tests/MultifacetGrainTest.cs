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
        private TimeSpan timeout = TimeSpan.FromSeconds(5);

        public MultifacetGrainTest(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        [Fact, TestCategory("Functional"), TestCategory("Cast")]
        public void RWReferences()
        {
            writer = GrainFactory.GetGrain<IMultifacetWriter>(GetRandomGrainId());
            reader = writer.AsReference<IMultifacetReader>();
            
            var x = 1234;
            var ok = writer.SetValue(x).Wait(timeout);
            if (!ok) throw new TimeoutException();
            var y = reader.GetValue().Result;
            Assert.Equal(x, y);
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void RWReferencesInvalidCastException()
        {
            Assert.Throws<InvalidCastException>(() =>
            {
                reader = GrainFactory.GetGrain<IMultifacetReader>(GetRandomGrainId());
                writer = (IMultifacetWriter)reader;
            });
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task MultifacetFactory()
        {
            var factory = GrainFactory.GetGrain<IMultifacetFactoryTestGrain>(GetRandomGrainId());
            var grain = GrainFactory.GetGrain<IMultifacetTestGrain>(GetRandomGrainId());
            var writer = await factory.GetWriter(grain /*"MultifacetFactory"*/);
            var reader = await factory.GetReader(grain /*"MultifacetFactory"*/);
            writer.SetValue(5).Wait();
            var v = reader.GetValue().Result;
            Assert.Equal(5, v);
            
        }

        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task Multifacet_InterfacesAsArguments()
        {
            var factory = GrainFactory.GetGrain<IMultifacetFactoryTestGrain>(GetRandomGrainId());
            var grain = GrainFactory.GetGrain<IMultifacetTestGrain>(GetRandomGrainId());
            factory.SetReader(grain).Wait();
            factory.SetWriter(grain).Wait();
            var writer = await factory.GetWriter();
            var reader = await factory.GetReader();
            writer.SetValue(10).Wait();
            var v = reader.GetValue().Result;
            Assert.Equal(10, v);
        }
    }
}
